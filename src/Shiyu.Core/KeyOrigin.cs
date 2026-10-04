namespace Shiyu.Core;

/// <summary>
/// 密钥的"来源"（票 29）：保存密钥时服务地址的 scheme + host + port。
///
/// 自备密钥只有一个格子，它是为哪家服务商填的得有个记号，否则切换预设
/// （或手改服务地址）之后，A 家的密钥就会作为 Authorization 头发给 B 家
/// 的服务器——DPAPI 保护的是密钥在磁盘上的样子，管不到它被发往哪里。
///
/// 只比来源、不比路径：同一家服务商常有多个路径前缀（/v1、
/// /compatible-mode/v1），改个路径不该让密钥失效；而不同的服务商从不共用
/// 主机。来源不是秘密，明文存进设置文件，所以用户信息（user:pass@）、
/// 查询串和片段一概不进来源。
///
/// 来源取自 <see cref="Uri"/>，与 HttpClient 发请求时解析地址的是同一个
/// 解析器：来源相等就意味着请求真的会去同一个 scheme、主机与端口。
/// </summary>
public static class KeyOrigin
{
    /// <summary>
    /// 服务地址的来源；空白地址没有来源（空串）。没写协议的地址按 https
    /// 读：这样的地址发不出任何请求（HttpClient 要绝对地址），拿它做来源
    /// 泄露不了什么，而用户补上 https:// 之后密钥仍然配得上，不必重填。
    /// </summary>
    public static string Of(string? baseUrl)
    {
        var text = baseUrl?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (TryHttp(text, out var uri) || (!HasScheme(text) && TryHttp("https://" + text, out uri)))
        {
            // Authority 是 主机[:端口]：不含用户信息，默认端口省略，主机已小写。
            return $"{uri.Scheme}://{uri.Authority}";
        }

        // 认不出主机的残缺地址（"https://"、"ftp://x"、端口写成字母）：同样发
        // 不出请求。给一个确定的、与任何真实来源都不相撞的值，而不是"没有来源"。
        return text.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>文本开头已经带了 <c>scheme://</c>——此时再补 https:// 只会补错。</summary>
    private static bool HasScheme(string text)
    {
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        return separator > 0
            && text[..separator].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    /// <summary>
    /// 来源里的主机（含非默认端口），给界面说"已保存的密钥属于 {旧主机}"。
    /// 认不出就是空串。
    /// </summary>
    public static string HostOf(string? origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Authority : string.Empty;

    private static bool TryHttp(string text, out Uri uri)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && parsed.Host.Length > 0)
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
