using System.Text;

namespace Shiyu.Core;

/// <summary>
/// 文字识别结果的拼法（评审 2026-10-16 F-07）。Windows 的识别按"词"给结果，中文却是一个字一个"词"，照常用空格
/// 拼起来就成了「你 好 世 界」。这里只在两边都是西文时才补空格：中文挨着中文、中文挨着标点都不留空。
/// </summary>
public static class OcrText
{
    /// <summary>一行里的词拼成一行字。</summary>
    public static string JoinWords(IEnumerable<string> words)
    {
        var line = new StringBuilder();
        string? previous = null;
        foreach (var word in words)
        {
            if (word.Length == 0)
            {
                continue;
            }

            if (previous is not null && NeedsSpace(previous, word))
            {
                line.Append(' ');
            }

            line.Append(word);
            previous = word;
        }

        return line.ToString();
    }

    /// <summary>各行拼成一段：一行一行，空行不要。</summary>
    public static string JoinLines(IEnumerable<string> lines)
        => string.Join("\n", lines.Select(line => line.Trim()).Where(line => line.Length > 0));

    private static bool NeedsSpace(string left, string right)
        => !IsWide(left[^1]) && !IsWide(right[0]);

    /// <summary>中日韩文字与全角标点：它们之间不用空格。</summary>
    private static bool IsWide(char c)
        => c is >= '⺀' and <= '鿿'   // CJK 部首、符号、标点、假名、汉字
            or >= '가' and <= '힯'    // 谚文
            or >= '豈' and <= '﫿'    // 兼容汉字
            or >= '＀' and <= '￯';   // 全角字符
}
