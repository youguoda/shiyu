namespace Shiyu.Core;

/// <summary>
/// 免费引擎的一次内部失败（网页形状变了、HTTP 非成功、令牌刷新后仍被拒……）。
/// 不进用户眼睛：后端把它接住、改走另一家，两家都挂时挂在「免费引擎暂时不可用」
/// 这句人话的内部异常上，供「复制错误详情」使用。消息里不带请求或译文原文。
/// </summary>
internal sealed class FreeEngineException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// 整段翻译器：一块文本进、一块译文出。免费引擎内部有两个——必应与腾讯——后端
/// 在它们之间做分块、兜底与冷却；它们自己不碰这些。语言以两家的码表项传入，
/// <paramref name="from"/> 为 null 表示让引擎自动检测。
/// </summary>
internal interface IWholeTextTranslator
{
    Task<string> TranslateAsync(
        string text,
        FreeEngineLanguage? from,
        FreeEngineLanguage to,
        CancellationToken cancellation);
}

/// <summary>
/// 两家网页接口要的"浏览器"请求头。伪造的 UA 与 Referer 只加在两个后端自己的
/// <see cref="HttpRequestMessage"/> 上——绝不写进 <see cref="HttpClients.Shared"/>
/// 的默认头，否则它会跟着发给所有大模型服务商。连接池照旧用 HttpClients.Shared。
/// </summary>
internal static class WebEngineHeaders
{
    /// <summary>必应一侧：Edge 的 UA（与网页自己的请求同一副面孔）。</summary>
    internal const string EdgeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";

    /// <summary>腾讯一侧：Chrome 的 UA。</summary>
    internal const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
}
