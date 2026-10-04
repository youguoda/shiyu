using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 免费引擎测试共用的假件（票 41）。单元测试一律不连网：必应页面来自真实页面
/// 裁出的夹具（Fixtures/bing-translator-page.html，令牌与 IG 已换成假值），
/// 其余应答由 <see cref="ScriptedHandler"/> 按请求形状回放。
/// </summary>
internal static class FreeEngineWeb
{
    public static string BingPageHtml
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bing-translator-page.html"));

    /// <summary>
    /// 必应页面的应答。<paramref name="finalUrl"/> 是"跳转之后"的地址——
    /// HttpClient 跟随跳转后把它留在 RequestMessage.RequestUri 上，假件手工摆上去。
    /// </summary>
    public static HttpResponseMessage BingPage(string? finalUrl = "https://cn.bing.com/translator", string? html = null)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(html ?? BingPageHtml, Encoding.UTF8, "text/html"),
            RequestMessage = finalUrl is null ? null : new HttpRequestMessage(HttpMethod.Get, finalUrl),
        };

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>必应翻译成功的应答：数组，首项带 translations[0].text 与 detectedLanguage。</summary>
    public static HttpResponseMessage BingTranslation(string text, string to = "en")
        => Json(new JsonArray(new JsonObject
        {
            ["detectedLanguage"] = new JsonObject { ["language"] = "zh-Hans", ["score"] = 1.0 },
            ["translations"] = new JsonArray(new JsonObject { ["text"] = text, ["to"] = to }),
            ["usedLLM"] = true,
        }).ToJsonString());

    /// <summary>必应的失败应答：HTTP 仍是 200，状态码在响应体里（205 = 令牌失效）。</summary>
    public static HttpResponseMessage BingStatus(int statusCode)
        => Json($$"""{"statusCode":{{statusCode}}}""");

    public static HttpResponseMessage TencentTranslation(params string[] lines)
        => Json(new JsonObject
        {
            ["header"] = new JsonObject { ["type"] = "auto_translation", ["ret_code"] = "succ" },
            ["auto_translation"] = new JsonArray(lines.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray()),
        }.ToJsonString());

    public static bool IsPage(HttpRequestMessage message)
        => message.Method == HttpMethod.Get && message.RequestUri!.AbsolutePath == "/translator";

    public static bool IsBingPost(HttpRequestMessage message)
        => message.Method == HttpMethod.Post && message.RequestUri!.AbsolutePath == "/ttranslatev3";

    public static bool IsTencent(HttpRequestMessage message)
        => message.Method == HttpMethod.Post && message.RequestUri!.AbsolutePath == "/api/imt";

    /// <summary>x-www-form-urlencoded 请求体 → 字段表（'+' 是空格，其余按 UTF-8 百分号解码）。</summary>
    public static Dictionary<string, string> Form(string body)
        => body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => Uri.UnescapeDataString((parts.Length > 1 ? parts[1] : string.Empty).Replace('+', ' ')));

    /// <summary>
    /// 按请求形状路由的 <see cref="ScriptedHandler"/>：<paramref name="route"/> 拿到
    /// 刚到的请求、已读出的请求体与全局调用序号。ScriptedHandler 先记录请求再
    /// 应答，所以闭包里读 Requests 是安全的。
    /// </summary>
    public static ScriptedHandler Routed(Func<HttpRequestMessage, string?, int, HttpResponseMessage> route)
    {
        ScriptedHandler handler = null!;
        handler = new ScriptedHandler(call =>
        {
            var (message, body) = handler.Requests[call - 1];
            return route(message, body, call);
        });
        return handler;
    }

    /// <summary>页面 + 必应翻译（译文由 <paramref name="translate"/> 按表单决定）的完整假站点。</summary>
    public static ScriptedHandler BingSite(Func<Dictionary<string, string>, string> translate)
        => Routed((message, body, _) =>
        {
            if (IsPage(message))
            {
                return BingPage();
            }

            var form = Form(body!);
            return BingTranslation(translate(form), form["to"]);
        });
}

/// <summary>什么都不回——靠调用方的取消/超时令牌把它叫醒。</summary>
internal sealed class HangingHandler : HttpMessageHandler
{
    public int Count;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Count);
        await Task.Delay(Timeout.Infinite, cancellation);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>
/// 第一个请求停在闸门前，其余照常通过——用来把"N 路并发共享一次页面抓取"
/// 钉成确定性的：闸门不开，在途请求就恰好是一个。
/// </summary>
internal sealed class GatedPageHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Open() => _gate.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellation)
    {
        Interlocked.Increment(ref _count);
        await _gate.Task.WaitAsync(cancellation);
        return FreeEngineWeb.BingPage();
    }
}

/// <summary>
/// 整段翻译器的假件：按 (文本, 第几次调用) 决定回什么或抛什么，并记下每次调用
/// 收到的内容——兜底、冷却、取消这些行为全靠它从外面观察。
/// </summary>
internal sealed class ScriptedTranslator(
    Func<string, int, CancellationToken, Task<string>> translate) : IWholeTextTranslator
{
    public List<string> Texts { get; } = [];

    public List<(FreeEngineLanguage? From, FreeEngineLanguage To)> Languages { get; } = [];

    public int Calls => Texts.Count;

    public Task<string> TranslateAsync(
        string text, FreeEngineLanguage? from, FreeEngineLanguage to, CancellationToken cancellation)
    {
        Texts.Add(text);
        Languages.Add((from, to));
        return translate(text, Texts.Count, cancellation);
    }

    /// <summary>每块译文 = 前缀 + 原块，足以认出"是谁译的、译的哪块"。</summary>
    public static ScriptedTranslator Prefixing(string prefix)
        => new((text, _, _) => Task.FromResult(prefix + text));

    /// <summary>原样吐回：用来证明切块与拼接不丢不添一个字。</summary>
    public static ScriptedTranslator Identity()
        => new((text, _, _) => Task.FromResult(text));

    public static ScriptedTranslator Failing(string reason = "boom")
        => new((_, _, _) => throw new FreeEngineException(reason));

    /// <summary>永不返回，直到令牌被取消（用户取消或单块超时）。</summary>
    public static ScriptedTranslator Hanging()
        => new(async (_, _, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return string.Empty;
        });
}
