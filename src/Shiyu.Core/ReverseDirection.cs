using System.Text;

namespace Shiyu.Core;

/// <summary>
/// 反向输入框的翻译语对（票 43）。方向只做中↔英，这是反向输入框的核心场景；其他外语留作后续。
/// </summary>
public enum ReversePair
{
    ChineseToEnglish,
    EnglishToChinese,
}

/// <summary>
/// 反向输入框"自动"方向的判定（票 43，取自 Xtranslate）：汉字数 ×5 ≥ 拉丁字母数 → 中→英，
/// 否则英→中。
///
/// 不用 <see cref="LanguageGuess"/> 的 ×2 权重，也不改它：面板的方向标签和回声换向的行为
/// 都有测试钉着。反向输入框里中文夹英文术语是常态，"帮我 fix 这个 bug in login.ts"
/// 按 ×2 算是英文，会被反过来译成中文；×5 才符合"这是一句中文，里面夹了几个词"。
/// 所以这是另一条独立的规则，字符分类按同一个口径写（汉字三个码位段、拉丁含带音标的字母），
/// 但与 LanguageGuess 互不依赖。
/// </summary>
public static class ReverseDirection
{
    /// <summary>一个汉字抵多少个拉丁字母：中文字少、信息密，英文单词里的字母多。</summary>
    public const int HanWeight = 5;

    /// <summary>
    /// 判定一段输入的方向。没有任何字母可判时（空串、纯数字、纯符号）按规则落在中→英：
    /// 0 × 5 ≥ 0。反向输入框是"往外写"的地方，默认出英文；空输入不会真的发请求。
    /// </summary>
    public static ReversePair Detect(string? text)
    {
        var han = 0;
        var latin = 0;

        foreach (var rune in (text ?? string.Empty).EnumerateRunes())
        {
            if (IsHan(rune.Value))
            {
                han++;
            }
            else if (IsLatinLetter(rune))
            {
                latin++;
            }
        }

        return han * HanWeight >= latin ? ReversePair.ChineseToEnglish : ReversePair.EnglishToChinese;
    }

    /// <summary>语对的（源语言，目标语言），写成 prompt 与 <see cref="TranslationRequest"/> 认的英文名。</summary>
    public static (string Source, string Target) Languages(ReversePair pair) => pair switch
    {
        ReversePair.EnglishToChinese => ("English", "Chinese"),
        _ => ("Chinese", "English"),
    };

    private static bool IsHan(int value)
        => value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF;

    // A–Z、a–z 与带音标的拉丁字母（含扩展 A/B、扩展附加）。0xC0–0x24F 里夹着 × 与 ÷，
    // 它们不是字母，所以再过一道 IsLetter。
    private static bool IsLatinLetter(Rune rune)
        => Rune.IsLetter(rune)
            && (rune.Value is >= 0x41 and <= 0x5A
                or >= 0x61 and <= 0x7A
                or >= 0xC0 and <= 0x24F
                or >= 0x1E00 and <= 0x1EFF);
}
