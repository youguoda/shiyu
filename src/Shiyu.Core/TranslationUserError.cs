using System.Text.RegularExpressions;

namespace Shiyu.Core;

/// <summary>翻译失败的类别（UI 报告 §6.2 错误文案映射表的六行）。</summary>
public enum TranslationErrorKind
{
    /// <summary>TLS / 证书校验失败（代理、抓包工具、系统时间）。</summary>
    Security,

    /// <summary>服务响应超时。</summary>
    Timeout,

    /// <summary>DNS 解析失败或网络不可达。</summary>
    Network,

    /// <summary>401 / 403：密钥无效或没有权限。</summary>
    Auth,

    /// <summary>429：请求过于频繁。</summary>
    RateLimit,

    /// <summary>其余一切。后端已用人话说清的（余额不足、还没配置）原样透传。</summary>
    Other,
}

/// <summary>
/// 面板失败态要显示的形状（票 22 / §6.2）：标题 + 一句人话说明 + 折叠的
/// 原始细节。原始异常永远不进标题和说明——那正是「中文前缀 + 原样透传的
/// .NET 英文异常」这个 P1 缺陷的来源；它只进 Raw，由界面收进折叠的
/// 「详细信息」和「复制错误详情」。
/// </summary>
public sealed record TranslationUserError(
    TranslationErrorKind Kind,
    string Title,
    string Detail,
    string Raw)
{
    /// <summary>表里有「重试」的类别；密钥类错误重试一万次也不会变好。</summary>
    public bool ShowRetry => Kind != TranslationErrorKind.Auth;

    /// <summary>表里有「服务设置」的类别：TLS 与密钥，改的都在设置里。</summary>
    public bool ShowSettings => Kind is TranslationErrorKind.Security or TranslationErrorKind.Auth;
}

/// <summary>
/// 异常 → 人话（§6.2 错误文案映射）。输入是会话留下的异常链（可能为 null，
/// 旧调用只有消息字符串）和消息；输出永远可显示：机器味的消息被映射表
/// 吞掉，只有人话的消息原样保留在后端精心写下的措辞里。
/// </summary>
public static class TranslationUserErrorMapper
{
    /// <summary>探针与界面共用的「机器味」判定：英文单词后跟两个以上小写词，
    /// 或异常类名。人话中文里嵌一个英文产品词（AllocationQuota）不会命中。</summary>
    internal static readonly Regex MachineMadePattern =
        new("[A-Za-z]{2,}(?:\\s+[a-z]{2,}){2,}|Exception", RegexOptions.Compiled);

    public static TranslationUserError Describe(Exception? failure, string? message)
    {
        var text = message ?? string.Empty;
        var kind = Classify(failure, text);

        return kind switch
        {
            TranslationErrorKind.Security => new(
                kind, "无法建立安全连接", "可能是代理、抓包工具或系统时间导致证书校验失败。", Raw(failure, text)),
            TranslationErrorKind.Timeout => new(
                kind, "服务响应超时", "网络较慢或服务繁忙。", Raw(failure, text)),
            TranslationErrorKind.Network => new(
                kind, "无法连接到翻译服务", "请检查网络连接。", Raw(failure, text)),
            TranslationErrorKind.Auth => new(
                kind, "密钥无效或没有权限", "请在设置中检查 API 密钥。", Raw(failure, text)),
            TranslationErrorKind.RateLimit => new(
                kind, "请求过于频繁", "稍等片刻再试。", Raw(failure, text)),
            _ => new(
                TranslationErrorKind.Other,
                "翻译失败",
                HumanDetail(text),
                Raw(failure, text)),
        };
    }

    /// <summary>其他类别的说明：人话原样（去掉「翻译失败：」前缀），机器味
    /// 换成表里的兜底句——绝不让 .NET 的英文原文顶到标题位。</summary>
    private static string HumanDetail(string message)
    {
        var detail = message.Trim();
        if (detail.StartsWith("翻译失败：", StringComparison.Ordinal))
        {
            detail = detail["翻译失败：".Length..];
        }

        return MachineMadePattern.IsMatch(detail) ? "服务返回了意外的响应。" : detail;
    }

    private static TranslationErrorKind Classify(Exception? failure, string message)
    {
        // 顺序即优先级：安全连接的异常包在 HttpRequestException 里，必须
        // 先于网络判；「还没有配置」含「凭据」二字，必须先于密钥判。
        if (Matches(failure, message, IsSecurity))
        {
            return TranslationErrorKind.Security;
        }

        if (message.Contains("还没有配置", StringComparison.Ordinal)
            || message.Contains("没有配置", StringComparison.Ordinal))
        {
            // 已经是人话且指向设置——保留措辞（Other），动作用「服务设置」。
            return TranslationErrorKind.Other;
        }

        // 免费引擎自己写的人话（票 41）：「改用自备密钥」「稍后再试」这类说法含
        // 密钥/超时等子串，落到下面几条会被误判——Auth 类还不给「重试」，
        // 并叫用户去检查根本不存在的 API 密钥。以「免费引擎」开头的一律归
        // Other，保留原措辞与「重试」；必须排在超时、429、Auth、网络之前
        // （TLS 仍然最先判，上面已过）。
        if (message.TrimStart().StartsWith(FreeEngineMessages.Prefix, StringComparison.Ordinal))
        {
            return TranslationErrorKind.Other;
        }

        if (message.Contains("超时", StringComparison.Ordinal)
            || ContainsType(failure, static ex => ex is TimeoutException)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return TranslationErrorKind.Timeout;
        }

        if (message.Contains("请求过于频繁", StringComparison.Ordinal)
            || message.Contains("429", StringComparison.Ordinal))
        {
            return TranslationErrorKind.RateLimit;
        }

        if (message.Contains("凭据", StringComparison.Ordinal)
            || message.Contains("密钥", StringComparison.Ordinal)
            || message.Contains("401", StringComparison.Ordinal)
            || message.Contains("403", StringComparison.Ordinal))
        {
            return TranslationErrorKind.Auth;
        }

        if (ContainsType(failure, static ex => ex is HttpRequestException or System.Net.Sockets.SocketException)
            || message.Contains("连接翻译服务失败", StringComparison.Ordinal)
            || message.Contains("连接公共通道失败", StringComparison.Ordinal))
        {
            return TranslationErrorKind.Network;
        }

        return TranslationErrorKind.Other;
    }

    /// <summary>TLS 失败的两种脸：类型是 AuthenticationException，或消息里
    /// 带着 SSL/TLS/certificate 字样（各 .NET 版本的措辞不一，按字面认）。</summary>
    private static bool IsSecurity(Exception ex)
        => ex.GetType().FullName == "System.Net.Security.AuthenticationException"
           || Regex.IsMatch(ex.Message, "SSL|TLS|certificate", RegexOptions.IgnoreCase);

    private static bool Matches(Exception? failure, string message, Func<Exception, bool> predicate)
        => ContainsType(failure, predicate) || (message.Length > 0 && predicate(new TranslationFailedException(message)));

    private static bool ContainsType(Exception? failure, Func<Exception, bool> predicate)
    {
        for (var ex = failure; ex is not null; ex = ex.InnerException)
        {
            if (predicate(ex))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>折叠「详细信息」里的原始内容：类型名 + 消息的异常链，
    /// 便于报障，不带堆栈（界面不是终端）。</summary>
    private static string Raw(Exception? failure, string message)
    {
        var parts = new List<string>();
        for (var ex = failure; ex is not null; ex = ex.InnerException)
        {
            var line = ex.GetType().Name + ": " + ex.Message;
            if (!parts.Contains(line))
            {
                parts.Add(line);
            }
        }

        return parts.Count > 0 ? string.Join("  <-  ", parts) : message;
    }
}
