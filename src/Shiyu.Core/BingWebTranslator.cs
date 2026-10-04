using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shiyu.Core;

/// <summary>必应翻译页面里翻译请求要用的四样东西（外加防滥用令牌的 TTL）。</summary>
/// <param name="TtlMs">令牌寿命，<b>毫秒</b>（今天是 3600000，即一小时）——别当成秒。</param>
internal sealed record BingPageValues(string Key, string Token, long TtlMs, string IG, string IID);

/// <summary>
/// 读必应翻译页面（规格照 Xtranslate 的 microsoft.js，已对 2026-10-04 的真实页面
/// 核实）。三处取值：<c>params_AbusePreventionHelper = [key, "token", ttl]</c>、
/// <c>IG:"hex"</c>、<b>第一个</b> <c>data-iid="…"</c>——页面里不止一个（今天有三个），
/// 翻译请求认的是 rich_tta 容器上那个，也就是第一个。脚本里的
/// <c>getAttribute("data-iid")</c> 字样没有等号，不会误中。
/// </summary>
internal static partial class BingPage
{
    [GeneratedRegex(@"params_AbusePreventionHelper\s*=\s*\[\s*(\d+)\s*,\s*""([^""]+)""\s*,\s*(\d+)\s*\]")]
    private static partial Regex AbusePrevention { get; }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])IG\s*:\s*""([A-Fa-f0-9]+)""")]
    private static partial Regex Ig { get; }

    [GeneratedRegex(@"data-iid\s*=\s*""([^""]+)""")]
    private static partial Regex Iid { get; }

    /// <summary>三项缺一项就是页面形状变了：返回 null，由上层改走腾讯，不凑合。</summary>
    internal static BingPageValues? Parse(string html)
    {
        var abuse = AbusePrevention.Match(html);
        var ig = Ig.Match(html);
        var iid = Iid.Match(html);

        if (!abuse.Success
            || !ig.Success
            || !iid.Success
            || !long.TryParse(abuse.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ttl))
        {
            return null;
        }

        return new BingPageValues(
            abuse.Groups[1].Value, abuse.Groups[2].Value, ttl, ig.Groups[1].Value, iid.Groups[1].Value);
    }

    /// <summary>
    /// 翻译请求的根地址：用<b>跳转之后</b>的主机（经代理访问 www.bing.com 会跳到 cn
    /// 之类）；主机不属于 *.bing.com、或不是 https，就退回 cn——被强制门户劫持的
    /// 页面不能把翻译请求（和用户的文本）引到别的主机去。
    /// </summary>
    internal static string TranslateBaseFor(Uri? finalUri)
        => finalUri is { Scheme: "https" } uri
            && (uri.Host == "bing.com" || uri.Host.EndsWith(".bing.com", StringComparison.OrdinalIgnoreCase))
                ? "https://" + uri.Host
                : "https://cn.bing.com";
}

/// <summary>一次取到的必应会话：令牌、IG、IID 与到期时刻。</summary>
internal sealed record BingSession(
    string Key, string Token, string IG, string IID, string TranslateBase, DateTimeOffset ExpiresAt);

/// <summary>
/// 必应会话缓存——放在后端实例<b>之外</b>的进程级持有者。<c>BuildTranslationBackend</c>
/// 每次翻译都新建一个后端，缓存挂在实例上就等于每次翻译都重新下载一遍 620KB 的
/// 页面。
///
/// 到期 <c>exp = 取到时刻 + max(ttl − 60s, 0)</c>；只在 <c>exp &gt; now + 15s</c> 时复用，
/// 所以快过期的会话会提前刷新。并发取会话共享同一个在途任务；在途的取页面
/// 与任何一个调用方的取消无关（有人放弃，别人还在等）。失败不缓存。
/// </summary>
internal sealed class BingSessionCache
{
    internal const string DefaultPageUrl = "https://cn.bing.com/translator";

    /// <summary>ttl 里要扣掉的安全边距。</summary>
    internal static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(60);

    /// <summary>离到期不足这么久就不再复用。</summary>
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(8);

    /// <summary>进程里唯一的一份：共享连接池，系统时钟。</summary>
    internal static BingSessionCache Shared { get; } = new(HttpClients.Shared, TimeProvider.System);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly string _pageUrl;
    private readonly TimeSpan _fetchTimeout;
    private readonly object _gate = new();
    private BingSession? _current;
    private Task<BingSession>? _inflight;

    internal BingSessionCache(
        HttpClient http,
        TimeProvider clock,
        string pageUrl = DefaultPageUrl,
        TimeSpan? fetchTimeout = null)
    {
        _http = http;
        _clock = clock;
        _pageUrl = pageUrl;
        _fetchTimeout = fetchTimeout ?? DefaultFetchTimeout;
    }

    /// <summary>会话缺失或已经快过期（预热据此决定要不要取）。</summary>
    internal bool NeedsFetch
    {
        get
        {
            lock (_gate)
            {
                return _current is null || !IsUsable(_current);
            }
        }
    }

    /// <summary>
    /// 一份能用的会话。<paramref name="stale"/> 是调用方刚被拒绝的那一份（401 或体内 205）：
    /// 缓存里若还是它，就强制重取；缓存里已经是更新的一份（别的并发请求先刷新了），
    /// 直接用新的，不再多下载一遍。
    /// </summary>
    internal Task<BingSession> GetAsync(BingSession? stale = null, CancellationToken cancellation = default)
    {
        Task<BingSession> pending;
        TaskCompletionSource<BingSession>? mine = null;

        lock (_gate)
        {
            if (_current is { } current && !ReferenceEquals(current, stale) && IsUsable(current))
            {
                return Task.FromResult(current);
            }

            if (_inflight is null)
            {
                mine = new TaskCompletionSource<BingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inflight = mine.Task;
            }

            pending = _inflight;
        }

        // 在锁外启动：假件或缓存的连接可能同步完成，同步完成的路径会回头抢这把锁。
        if (mine is not null)
        {
            _ = RunFetchAsync(mine);
        }

        return pending.WaitAsync(cancellation);
    }

    /// <summary>
    /// 预热：失败不提示、不抛——真正翻译时会再取一次，那里的失败才算数。
    /// </summary>
    internal async Task WarmAsync()
    {
        try
        {
            await GetAsync();
        }
        catch (Exception)
        {
            // expected: 预热失败静默；会话没取到，翻译那一刻自会再取并如实报错。
        }
    }

    private bool IsUsable(BingSession session)
        => session.ExpiresAt > _clock.GetUtcNow() + RefreshMargin;

    private async Task RunFetchAsync(TaskCompletionSource<BingSession> promise)
    {
        try
        {
            var session = await FetchAsync();

            lock (_gate)
            {
                _current = session;
                _inflight = null;
            }

            promise.SetResult(session);
        }
        catch (Exception failure)
        {
            // expected: 失败交给等它的人（promise）；没人等时也要把异常标记为已观察，
            // 免得一次静默的预热失败漂成未观察任务。失败不缓存，下一个调用方重试。
            lock (_gate)
            {
                _inflight = null;
            }

            promise.SetException(failure);
            _ = promise.Task.Exception;
        }
    }

    private async Task<BingSession> FetchAsync()
    {
        using var timeout = new CancellationTokenSource(_fetchTimeout);
        using var message = new HttpRequestMessage(HttpMethod.Get, _pageUrl);
        message.Headers.UserAgent.ParseAdd(WebEngineHeaders.EdgeUserAgent);

        try
        {
            using var response = await _http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new FreeEngineException($"必应页面返回 HTTP {(int)response.StatusCode}");
            }

            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            var values = BingPage.Parse(html)
                ?? throw new FreeEngineException("必应页面的形状变了：找不到令牌、IG 或 data-iid");

            // ttl 是毫秒；减去一分钟的边距，最少为零，最多按一天封顶（防怪值溢出）。
            var lifetime = TimeSpan.FromMilliseconds(Math.Clamp(
                values.TtlMs - (long)SafetyMargin.TotalMilliseconds, 0, (long)TimeSpan.FromDays(1).TotalMilliseconds));

            return new BingSession(
                values.Key,
                values.Token,
                values.IG,
                values.IID,
                BingPage.TranslateBaseFor(response.RequestMessage?.RequestUri),
                _clock.GetUtcNow() + lifetime);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("取必应会话超时");
        }
    }
}

/// <summary>
/// 必应网页接口的整段翻译器（票 41）。POST <c>{翻译地址}/ttranslatev3?isVertical=1&amp;IG=…&amp;IID=…</c>，
/// 表单字段 fromLang（<c>auto-detect</c> 或映射后的码）、to、text、token、key。成功的
/// 应答是 <c>[{translations:[{text,to}], detectedLanguage:{language}}]</c>。
///
/// 令牌失效有两种脸：HTTP 401，或——更常见——HTTP 200 而响应体是
/// <c>{"statusCode":205}</c>（205 在体里，不是 HTTP 状态码）。两者都强刷会话后重试
/// <b>恰好一次</b>，第二次仍被拒就报错，不循环；其余 statusCode ≠ 200 一律算失败。
/// 实测不需要 cookie。
/// </summary>
internal sealed class BingWebTranslator(HttpClient http, BingSessionCache sessions) : IWholeTextTranslator
{
    public async Task<string> TranslateAsync(
        string text, FreeEngineLanguage? from, FreeEngineLanguage to, CancellationToken cancellation)
    {
        var session = await sessions.GetAsync(cancellation: cancellation);
        var outcome = await PostAsync(session, text, from, to, cancellation);

        if (outcome.TokenRejected)
        {
            session = await sessions.GetAsync(session, cancellation);
            outcome = await PostAsync(session, text, from, to, cancellation);

            if (outcome.TokenRejected)
            {
                throw new FreeEngineException("必应在刷新会话之后仍拒绝令牌");
            }
        }

        return outcome.Text!;
    }

    private readonly record struct Outcome(string? Text, bool TokenRejected);

    private async Task<Outcome> PostAsync(
        BingSession session,
        string text,
        FreeEngineLanguage? from,
        FreeEngineLanguage to,
        CancellationToken cancellation)
    {
        var address = $"{session.TranslateBase}/ttranslatev3"
            + $"?isVertical=1&IG={Uri.EscapeDataString(session.IG)}&IID={Uri.EscapeDataString(session.IID)}";

        using var message = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new FormUrlEncodedContent(
            [
                new("fromLang", from?.Bing ?? "auto-detect"),
                new("text", text),
                new("to", to.Bing),
                new("token", session.Token),
                new("key", session.Key),
            ]),
        };

        // 伪造的 UA/Referer 只在这条请求上，不进共享客户端的默认头。
        message.Headers.UserAgent.ParseAdd(WebEngineHeaders.EdgeUserAgent);
        message.Headers.Referrer = new Uri($"{session.TranslateBase}/translator");

        using var response = await http.SendAsync(message, cancellation);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new Outcome(null, TokenRejected: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new FreeEngineException($"必应返回 HTTP {(int)response.StatusCode}");
        }

        return Interpret(await response.Content.ReadAsStringAsync(cancellation));
    }

    /// <summary>读 200 应答的体：数组是译文；带 statusCode 的对象是失败（205 = 令牌失效）。</summary>
    private static Outcome Interpret(string body)
    {
        try
        {
            switch (JsonNode.Parse(body))
            {
                case JsonArray results:
                    if (results.Count > 0
                        && results[0]?["translations"] is JsonArray { Count: > 0 } translations
                        && translations[0]?["text"]?.GetValue<string>() is { } text)
                    {
                        return new Outcome(text, TokenRejected: false);
                    }

                    break;

                case JsonObject failure when failure["statusCode"] is { } code:
                    if (code.GetValue<int>() == 205)
                    {
                        return new Outcome(null, TokenRejected: true);
                    }

                    throw new FreeEngineException($"必应在应答体里给出 statusCode {code.GetValue<int>()}");
            }
        }
        catch (JsonException unreadable)
        {
            throw new FreeEngineException("必应的应答不是 JSON", unreadable);
        }
        catch (InvalidOperationException shape)
        {
            throw new FreeEngineException("必应的应答形状不对", shape);
        }
        catch (FormatException shape)
        {
            throw new FreeEngineException("必应的应答形状不对", shape);
        }

        throw new FreeEngineException("必应的应答里没有译文");
    }
}
