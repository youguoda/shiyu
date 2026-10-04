using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiyu.Core;

/// <summary>
/// 公共通道的端点与设备身份。刻意没有密钥字段——凭据留在服务端是这条
/// 通道的全部意义，客户端只带着匿名安装 ID 来领每天那份免费额度。
/// </summary>
public sealed record RelayBackendOptions(string Endpoint, string ClientId)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}

/// <summary>
/// 走拾语公共通道的翻译后端：与 <see cref="OpenAiCompatibleBackend"/> 同
/// 接口，但上游经中转到达智谱免费档，客户端零密钥。
///
/// 线上事实是上游非流式——中转把整句译文一次回完。这里在客户端化妆成
/// 流式：整段答案按句切开、按小步节奏吐出，面板的流式观感因此与自备
/// 密钥路径一致。切片只切不删，拼回去与服务端译文一字不差。
/// </summary>
public sealed class RelayBackend(
    RelayBackendOptions options,
    HttpClient? httpClient = null,
    Func<TimeSpan>? transientBackoff = null,
    Func<TimeSpan>? pieceDelay = null) : ITranslationBackend, IDisposable
{
    /// <summary>瞬态失败最多整发三次，与自备密钥路径共用同一纪律（票 34）。</summary>
    private const int MaxAttempts = 3;

    /// <summary>单请求字符上限，与服务端同一数字（Glossy 实证参数）。</summary>
    internal const int MaxRequestChars = 2000;

    /// <summary>切片器已抽到 <see cref="StreamingSlicer"/>（票 41，与免费引擎共用）；这里只留转发。</summary>
    internal const int MaxPieceChars = StreamingSlicer.MaxPieceChars;

    /// <summary>
    /// 官方通道地址。部署状态见 server/README.md——地址在这里、设置文件
    /// 与部署文档三处一致，改它要三处一起改。
    /// </summary>
    public const string DefaultEndpoint = "https://shiyu-relay.workers.dev";

    private readonly HttpClient _http = httpClient ?? new HttpClient();
    private readonly bool _ownsClient = httpClient is null;
    private readonly Func<TimeSpan> _transientBackoff = transientBackoff
        ?? OpenAiCompatibleBackend.NextTransientBackoff;
    private readonly Func<TimeSpan> _pieceDelay = pieceDelay
        ?? DefaultPieceDelay;

    /// <summary>片间小步节奏：够让译文"正在到来"，不至于拖慢总和。</summary>
    private static TimeSpan DefaultPieceDelay() => StreamingSlicer.PieceInterval;

    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        if (!options.IsConfigured)
        {
            throw new TranslationFailedException("公共通道还没有配置地址，请在设置里换一种翻译方式。");
        }

        // 服务端同款检查前置到客户端：一次注定被拒的往返不值当发出去。
        if (request.Text.Length > MaxRequestChars)
        {
            throw new TranslationFailedException($"单次最多翻译 {MaxRequestChars} 字，请把文本分段后再试。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(options.Timeout);

        var translation = await FetchTranslationAsync(request, timeout, cancellation);
        if (translation.Length == 0)
        {
            yield break;
        }

        var pieces = SplitForStreaming(translation);
        for (var i = 0; i < pieces.Count; i++)
        {
            if (i > 0)
            {
                try
                {
                    await Task.Delay(_pieceDelay(), cancellation);
                }
                catch (OperationCanceledException)
                {
                    // 用户在半途叫停：已到的译文留下，剩下的不再来。
                    yield break;
                }
            }

            yield return pieces[i];
        }
    }

    /// <summary>
    /// 发送并按票 34 的纪律处理失败：瞬态码退避重试，配额与请求类错误
    /// 一次就给人话。重试预算挂在同一条超时链上，面板的等待有上界。
    /// </summary>
    private async Task<string> FetchTranslationAsync(
        TranslationRequest request,
        CancellationTokenSource timeout,
        CancellationToken cancellation)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            using (var message = BuildRequest(request))
            {
                try
                {
                    response = await _http.SendAsync(
                        message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                }
                catch (HttpRequestException network)
                {
                    throw new TranslationFailedException($"连接公共通道失败：{network.Message}", network);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new TranslationFailedException("公共通道响应超时。");
                }
            }

            if (response.IsSuccessStatusCode)
            {
                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    return ParseTranslation(body);
                }
            }

            var described = await DescribeFailure(response, timeout.Token);
            var status = (int)response.StatusCode;
            response.Dispose();

            // 429 在这条通道上只意味着配额拒绝，重试不会多出一个字；
            // 其余瞬态码沿用票 34 的窗口与次数。
            var transient = status != 429 && OpenAiCompatibleBackend.IsTransient(status);
            if (!transient || attempt >= MaxAttempts)
            {
                throw new TranslationFailedException(described);
            }

            try
            {
                await Task.Delay(_transientBackoff(), timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TranslationFailedException("公共通道响应超时。");
            }
        }
    }

    private HttpRequestMessage BuildRequest(TranslationRequest request)
    {
        var body = new JsonObject
        {
            ["clientId"] = options.ClientId,
            ["text"] = request.Text,
            ["from"] = request.SourceLanguage is { Length: > 0 } declared ? declared : null,
            ["to"] = request.TargetLanguage,
        };

        return new HttpRequestMessage(
            HttpMethod.Post, $"{options.Endpoint.TrimEnd('/')}/translate")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// Turns a failed response into something a user can act on. 配额拒绝
    /// 带着剩余量与重置时间到达，翻译成"还剩多少、何时恢复"——这是通道
    /// 对用户最起码的诚实。
    /// </summary>
    private static async Task<string> DescribeFailure(
        HttpResponseMessage response, CancellationToken cancellation)
    {
        var status = (int)response.StatusCode;

        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellation);
            if (body.Length is > 0 and < 400)
            {
                detail = DescribeErrorBody(body);
            }
        }
        catch (Exception)
        {
            // expected: 读错误体失败——状态码本身仍值得报告。
        }

        return detail ?? status switch
        {
            429 => "翻译失败：今日免费额度已用完，明天再来。",
            404 => "翻译失败：公共通道地址不正确。",
            >= 500 => "翻译失败：公共通道暂时不可用，稍后再试。",
            _ => $"翻译失败：公共通道返回 {status}。",
        };
    }

    /// <summary>把错误体里的 message/remaining/resetAt 拼成一句话；拼不出返回 null。</summary>
    private static string? DescribeErrorBody(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            if (error is null)
            {
                return null;
            }

            var message = error["message"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            var builder = new StringBuilder("翻译失败：").Append(message);
            if (error["remaining"] is { } remaining
                && DateTimeOffset.TryParse(
                    error["resetAt"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal,
                    out var resetAt))
            {
                builder.Append('。')
                    .Append($"剩余 {remaining} 字，{resetAt.ToLocalTime():M月d日 HH:mm} 恢复。");
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string ParseTranslation(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["translation"]?.GetValue<string>() ?? string.Empty;
        }
        catch (JsonException)
        {
            throw new TranslationFailedException("公共通道返回了无法理解的内容，稍后再试。");
        }
        catch (InvalidOperationException)
        {
            throw new TranslationFailedException("公共通道返回了无法理解的内容，稍后再试。");
        }
        catch (KeyNotFoundException)
        {
            throw new TranslationFailedException("公共通道返回了无法理解的内容，稍后再试。");
        }
    }

    /// <summary>
    /// 把整段译文切成按序吐出的片段。逻辑在 <see cref="StreamingSlicer"/>——免费引擎
    /// 与公共通道走同一个函数（票 41），这里只留转发，既有测试照旧经它调用。
    /// </summary>
    internal static IReadOnlyList<string> SplitForStreaming(string text) => StreamingSlicer.Split(text);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
