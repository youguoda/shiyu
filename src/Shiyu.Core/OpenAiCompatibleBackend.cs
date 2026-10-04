using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiyu.Core;

/// <param name="BaseUrl">Endpoint root, e.g. a provider's OpenAI-compatible base.</param>
/// <param name="ApiKey">Never logged, never shown in the interface.</param>
public sealed record TranslationBackendOptions(string BaseUrl, string Model, string ApiKey)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Talks to any endpoint speaking the OpenAI chat-completions shape, which by
/// now includes most providers worth pointing this at.
///
/// Chosen over a provider-specific client so that swapping backends is a
/// settings change rather than a code change — the port exists precisely so
/// this class can be replaced without anything else noticing.
///
/// 三个可选参数是服务商预设（票 08）落在传输层的形状：各家"关思考"的
/// 扩展字段、温度上限或不发温度。全部缺省时行为与从前一字不差。
/// </summary>
/// <param name="extraBody">附加到请求体顶层的字段（如 DeepSeek 的 thinking.type=disabled）。</param>
/// <param name="maxTemperature">温度上限：实际发送 Min(请求值, 该值)。</param>
/// <param name="sendTemperature">false 时整个 temperature 字段不发（Kimi 的温度是固定值，传错报错）。</param>
/// <param name="firstByteTimeout">从发请求到第一个字节的最长等待（含瞬态重试的退避）。</param>
/// <param name="idleTimeout">流式响应相邻两块之间的最长间隔——限"卡住"，不限"长"（O-23）。</param>
public sealed class OpenAiCompatibleBackend(
    TranslationBackendOptions options,
    HttpClient? httpClient = null,
    Func<TimeSpan>? transientBackoff = null,
    JsonObject? extraBody = null,
    double? maxTemperature = null,
    bool sendTemperature = true,
    TimeSpan? firstByteTimeout = null,
    TimeSpan? idleTimeout = null) : ITranslationBackend, IStreamingModel, IDisposable
{
    /// <summary>瞬态失败最多退避重试两次（即整发三次）。</summary>
    private const int MaxAttempts = 3;

    private static readonly TimeSpan DefaultFirstByte = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultIdle = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http = httpClient ?? new HttpClient();
    private readonly bool _ownsClient = httpClient is null;
    private readonly Func<TimeSpan> _transientBackoff = transientBackoff ?? NextTransientBackoff;
    private readonly TimeSpan _firstByteTimeout = firstByteTimeout ?? DefaultFirstByte;
    private readonly TimeSpan _idleTimeout = idleTimeout ?? DefaultIdle;

    /// <summary>
    /// 抽样一次默认退避时长。窗口 [600,1400]ms 取自 Glossy 对上游瞬态
    /// 错误的实证：够对端喘口气，又不让面板干等到像卡死。
    /// </summary>
    internal static TimeSpan NextTransientBackoff()
        => TimeSpan.FromMilliseconds(Random.Shared.Next(600, 1401));

    public IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellation)
    {
        // 提示词模板（票 42）：system、user 两段与温度都由模板决定，ModelRequest 的
        // 形状不变——示例写在 system 里，不引入多轮假对话。
        var prompt = TranslationPrompt.Build(request);
        return StreamAsync(
            new ModelRequest(prompt.System, prompt.User)
            {
                Temperature = prompt.Temperature,
            },
            cancellation);
    }

    /// <summary>
    /// The one transport. Translation and agent actions both come through here
    /// rather than each having its own way to reach a model.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        if (!options.IsConfigured)
        {
            throw new TranslationFailedException("还没有配置翻译后端：请先在设置中填写接口地址、模型与凭据。");
        }

        // 超时拆成两半（O-23）：首字节与"块间停顿"各限各的，总时长不再设
        // 上限——此前 30 秒一刀切会把长译文的尾巴截掉。调用方（预算外壳、
        // 用户取消）的 token 仍然管全程。
        var response = await SendWithRetryAsync(request, cancellation);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationFailedException(await DescribeFailure(response, cancellation));
            }

            await using var stream = new ChunkTimeoutStream(
                await response.Content.ReadAsStreamAsync(cancellation),
                _firstByteTimeout,
                _idleTimeout);

            await foreach (var payload in ServerSentEvents.ReadDataAsync(stream, cancellation))
            {
                if (ExtractContent(payload) is { Length: > 0 } piece)
                {
                    yield return piece;
                }
            }
        }
    }

    /// <summary>
    /// A stream that bounds waiting, not length: the first read must start
    /// within <paramref name="firstByteTimeout"/>, every later read within
    /// <paramref name="idleTimeout"/> of the previous chunk. A translation
    /// may take as long as it keeps making progress.
    /// </summary>
    private sealed class ChunkTimeoutStream(
        Stream inner, TimeSpan firstByteTimeout, TimeSpan idleTimeout) : Stream
    {
        private bool _firstRead = true;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellation)
        {
            var budget = _firstRead ? firstByteTimeout : idleTimeout;
            _firstRead = false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(budget);
            try
            {
                return await inner.ReadAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // Cancelling mid-read poisons the stream — and we are about to
                // throw our way out of it anyway.
                throw new TranslationFailedException("翻译服务响应超时。");
            }
            catch (IOException failure) when (timeout.IsCancellationRequested)
            {
                // Some stacks surface the abort as an IO error instead.
                throw new TranslationFailedException("翻译服务响应超时。", failure);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // The async path is the only one translation ever takes; the
            // sync override exists to satisfy Stream and stays unguarded.
            return inner.Read(buffer, offset, count);
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
    }

    /// <summary>
    /// 发送并对瞬态状态码整发重试。请求消息逐次重建——HttpClient 不允许
    /// 同一条消息发两次；首字节预算覆盖三次尝试与退避（瞬态失败叠加慢首
    /// 字节，用户等的还是同一个"服务响应超时"，不必区分是哪一层慢的）。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        ModelRequest request,
        CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(_firstByteTimeout);

        HttpResponseMessage response;
        for (var attempt = 1; ; attempt++)
        {
            using var message = BuildRequest(request);
            try
            {
                response = await _http.SendAsync(
                    message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (HttpRequestException network)
            {
                throw new TranslationFailedException($"连接翻译服务失败：{network.Message}", network);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // The linked token fired, not the caller's: this is the timeout.
                throw new TranslationFailedException("翻译服务响应超时。");
            }

            if (response.IsSuccessStatusCode
                || !IsTransient((int)response.StatusCode)
                || attempt >= MaxAttempts)
            {
                return response;
            }

            response.Dispose();

            try
            {
                await Task.Delay(_transientBackoff(), timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // 重试还没发出去预算先耗尽：这与首射超时对用户是同一件事。
                throw new TranslationFailedException("翻译服务响应超时。");
            }
        }
    }

    /// <summary>
    /// 瞬态状态码：限流（429）、过载（529，DeepSeek/Anthropic 系）与常见
    /// 网关故障。凭据、余额、路径类错误重试一万次也不会变好，不在此列。
    /// </summary>
    internal static bool IsTransient(int statusCode)
        => statusCode is 408 or 429 or 500 or 502 or 503 or 504 or 529;

    private HttpRequestMessage BuildRequest(ModelRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = true,

            // Translation wants the likeliest rendering, not an interesting one.
            // Presets may clamp the value to the provider's legal range, or
            // forbid sending it at all — Kimi's is a fixed number and rejects
            // anything else.
            ["temperature"] = Math.Min(request.Temperature, maxTemperature ?? request.Temperature),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = request.SystemPrompt,
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = request.UserContent,
                },
            },
        };

        if (!sendTemperature)
        {
            body.Remove("temperature");
        }

        // The preset's provider-specific fields ride on top: deep-cloned so a
        // shared preset object cannot be mutated through a request body.
        if (extraBody is { Count: > 0 })
        {
            foreach (var (name, value) in extraBody)
            {
                body[name] = value?.DeepClone();
            }
        }

        var message = new HttpRequestMessage(
            HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return message;
    }

    /// <summary>
    /// Turns a failed response into something a user can act on, without ever
    /// echoing the credential back at them. 凭据与余额类终态错误只给人话：
    /// 服务商响应体里的原文对用户既不可读也不可行动。
    /// </summary>
    private static async Task<string> DescribeFailure(
        HttpResponseMessage response, CancellationToken cancellation)
    {
        var status = (int)response.StatusCode;
        var terminal = status is 401 or 402 or 403;

        var reason = status switch
        {
            401 or 403 => "凭据无效或已过期，请检查设置里的接口密钥",
            402 => "账户余额不足，请到服务商处充值",
            404 => "接口地址或模型名不正确",
            429 => "请求过于频繁，稍后再试",
            >= 500 => "翻译服务暂时不可用",
            _ => $"翻译服务返回 {status}",
        };

        if (terminal)
        {
            // 百炼的未实名账号免费额度耗尽会以 403 AllocationQuota.FreeTierOnly
            // 回来：密钥是好的，拒绝的是额度——把它误报成"凭据无效"会把
            // 用户支去改一个没问题的密钥。只认这一个错误码，其余终态错误
            // 的响应体依旧不进文案。
            string? terminalBody = null;
            try
            {
                terminalBody = await response.Content.ReadAsStringAsync(cancellation);
            }
            catch (Exception)
            {
                // expected: the status code alone is still worth reporting;
                // the body is best-effort context, never load-bearing.
            }

            if (terminalBody?.Contains("AllocationQuota.FreeTierOnly", StringComparison.Ordinal) == true)
            {
                return "翻译失败：免费额度已用完，请实名或充值。";
            }

            return $"翻译失败：{reason}。";
        }

        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellation);
            if (body.Length is > 0 and < 400)
            {
                detail = body;
            }
        }
        catch (Exception)
        {
            // expected: 读错误体失败——状态码本身仍值得报告。
        }

        return detail is null ? $"翻译失败：{reason}。" : $"翻译失败：{reason}。{detail}";
    }

    /// <summary>
    /// Pulls the text out of one streamed chunk. A chunk that does not parse is
    /// skipped rather than aborting the stream: providers interleave frames
    /// carrying usage statistics and other bookkeeping.
    /// </summary>
    internal static string? ExtractContent(string payload)
    {
        try
        {
            var choices = JsonNode.Parse(payload)?["choices"]?.AsArray();
            if (choices is null || choices.Count == 0)
            {
                return null;
            }

            var first = choices[0];
            return first?["delta"]?["content"]?.GetValue<string>()
                ?? first?["message"]?["content"]?.GetValue<string>();
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

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
