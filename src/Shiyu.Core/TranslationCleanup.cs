using System.Text.RegularExpressions;

namespace Shiyu.Core;

/// <summary>
/// Strips the packaging a general model wraps around a translation despite
/// being asked not to.
///
/// The prompt is the real defence; this is the belt to its braces. It removes
/// only unmistakable wrappers — a leading "Translation:" label, a trailing
/// "Note:" paragraph — and leaves anything ambiguous alone, because mangling a
/// correct translation is worse than passing a slightly chatty one through.
/// </summary>
public static partial class TranslationCleanup
{
    [GeneratedRegex(@"^\s*(?:translation|translated text|译文|翻译)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingLabel { get; }

    [GeneratedRegex(@"\n\s*(?:note|notes|explanation|注|注释|说明)\s*[:：].*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TrailingNote { get; }

    // 模板的 user 段用 <text> 包裹时，模型偶尔把包装原样回显（票 42）。只认首尾：
    // 正文里同样的字面不动，因为原文本身可能就是 XML。
    [GeneratedRegex(@"^\s*<text>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingWrapper { get; }

    [GeneratedRegex(@"\s*</text>\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingWrapper { get; }

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = LeadingLabel.Replace(text.Trim(), string.Empty);
        cleaned = TrailingNote.Replace(cleaned, string.Empty);

        return Unquote(cleaned.Trim());
    }

    /// <summary>
    /// 按模板的类别清洗（票 42）：
    /// <list type="bullet">
    /// <item>标准：与从前一字不差——它的输入不包裹，译文里的 &lt;text&gt; 是正文；</item>
    /// <item>口语、正式：先剥回显的 &lt;text&gt; 包装，再走全量清洗；</item>
    /// <item>改写类：<b>只</b>剥首尾的 &lt;text&gt;/&lt;/text&gt;。优化后的提示词里，
    /// "Notes:" 一段可能就是正文，引号与"译文："也可能是正文，一个字都不能动。</item>
    /// </list>
    /// </summary>
    public static string Clean(string text, PromptTemplate template)
        => TranslationPrompt.ShapeOf(template) switch
        {
            TranslationPrompt.PromptShape.Rewrite => CleanRewrite(text),
            TranslationPrompt.PromptShape.Colloquial or TranslationPrompt.PromptShape.Formal
                => CleanWrapped(text),
            _ => Clean(text),
        };

    private static string CleanRewrite(string text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : StripWrapper(text.Trim()).Trim();

    /// <summary>
    /// 包装可能夹在标签、注释前后（"Translation: &lt;text&gt;…&lt;/text&gt;"、"&lt;/text&gt;\nNote: …"），
    /// 所以全量清洗前后各走一遍：先让标签与尾注露出包装，再剥包装，再清洗里面。
    /// </summary>
    private static string CleanWrapped(string text)
        => Clean(StripWrapper(Clean(text)));

    private static string StripWrapper(string text)
        => TrailingWrapper.Replace(LeadingWrapper.Replace(text, string.Empty), string.Empty);

    /// <summary>
    /// Removes quotes the model added around the whole result — but only when
    /// they wrap everything, so a translation that genuinely contains a quoted
    /// phrase survives intact.
    /// </summary>
    private static string Unquote(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }

        var opens = text[0];
        var closes = text[^1];

        var wrapped =
            (opens == '"' && closes == '"')
            || (opens == '\'' && closes == '\'')
            || (opens == '“' && closes == '”')
            || (opens == '「' && closes == '」');

        if (!wrapped)
        {
            return text;
        }

        var inner = text[1..^1];
        return inner.Contains(opens) || inner.Contains(closes) ? text : inner;
    }
}
