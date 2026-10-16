using System.Text.RegularExpressions;

namespace DictionaryBuilder;

/// <summary>
/// 识别"整条释义只是在说：本词是某个词的变形"。ECDICT 里 went 的释义就是 "go的过去式"，
/// examples 的是 "n. 例子；列证（example的复数形式）"。这种条目没有自己的内容，
/// 查 went 时应该直接给 go 的释义，所以它们不当词头收，改走 forms 表。
///
/// 规则故意收得很窄（宁可漏判，不可错判）：
///   * 每一行都必须是"说明行"，只要有一行带着自己的释义（如 gone 的 "a. 离去的"），整条就不算；
///   * 说明行只有两种写法：整行就是说明，或者说明单独放在行尾的括号里；
///     "n. 存在, 性质, 生命, be的现在分词" 这种混在释义列表里的写法不算；
///   * 各行必须指向同一个原形。
/// </summary>
internal static partial class InflectionNote
{
    // 说明部分：以变形类型的关键词开头（过去式、复数……），后面允许跟"和过去分词""形式"之类的尾巴，
    // 但不能再出现逗号、分号、括号——那说明里面还混着别的释义。
    private const string Kind =
        @"(?:名词|动词)?(?:过去式|过去分词|过去时|现在分词|第三人称单数|三单|单三|复数|比较级|最高级|ing形式)[^,;，；（）()]*";

    private const string Lemma = @"([A-Za-z][A-Za-z'\-]*)";

    // 整行就是说明，可以带词性前缀：go的过去式、pl. man的复数、have的过去式和过去分词
    [GeneratedRegex(@"^(?:[a-z]{1,5}\.\s*)?" + Lemma + @"\s*的\s*" + Kind + "$")]
    private static partial Regex WholeLine();

    // 说明放在行尾的括号里：n. 例子；列证（example的复数形式）、v. 猜( guess的现在分词 )
    [GeneratedRegex(@"[（(]\s*" + Lemma + @"\s*的\s*" + Kind + @"[）)]\s*$")]
    private static partial Regex Parenthesized();

    /// <summary>
    /// 如果释义的每一行都是"X 的变形"说明且都指向同一个 X，返回 X（小写）；否则返回 null。
    /// </summary>
    public static string? StubLemma(string translation)
    {
        string? lemma = null;
        foreach (var line in translation.Split('\n'))
        {
            var match = WholeLine().Match(line);
            if (!match.Success) match = Parenthesized().Match(line);
            if (!match.Success) return null; // 这一行有自己的内容

            var name = match.Groups[1].Value.ToLowerInvariant();
            if (lemma is not null && lemma != name) return null; // 各行指向不同的词，不敢断言
            lemma = name;
        }
        return lemma;
    }

    // ---- 安全网：变形条目的释义有没有原形给不出的内容 ----------------------------------------

    // 说明之外的释义里，每行开头的词性（n. vt. pl.）和标签（[计]、<正式>、（使））都不算释义本身。
    [GeneratedRegex(@"^\s*(?:[a-z]{1,6}\.\s*)?(?:(?:\[[^\]]*\]|<[^>]*>|（[^）]*）|\([^)]*\))\s*)*")]
    private static partial Regex LeadingLabels();

    private static readonly char[] ItemSeparators = ['；', ';', ',', '，', '、', '/', '（', '(', '['];

    /// <summary>
    /// 变形条目的释义是不是"原形的释义已经包含了的"。
    /// 有些复数有自己独立的义项：glasses 的第一义是"眼镜"，而 glass 的释义里没有眼镜，
    /// 这时把 glasses 并到 glass 会丢信息，所以不能降级。
    /// 判法：取变形条目每一行的第一个释义项（说明之外），看它是否出现在原形释义里；
    /// 较长的项（≥3 字）只要有任意相邻两字出现即可（"意外事故" 对 "事故"）。
    /// 只有说明、没有自己释义的行（went：go的过去式）不影响结果。
    /// </summary>
    public static bool GlossCoveredBy(string stubTranslation, string lemmaTranslation)
    {
        foreach (var line in stubTranslation.Split('\n'))
        {
            var primary = PrimaryGloss(line);
            if (primary.Length == 0) continue;
            if (!IsCovered(primary, lemmaTranslation)) return false;
        }
        return true;
    }

    /// <summary>一行里说明之外的第一个释义项，只留汉字；没有则返回空串。</summary>
    private static string PrimaryGloss(string line)
    {
        string gloss;
        var parenthesized = Parenthesized().Match(line);
        if (parenthesized.Success) gloss = line[..parenthesized.Index];
        else if (WholeLine().IsMatch(line)) return "";
        else gloss = line;

        gloss = LeadingLabels().Replace(gloss, "");
        var end = gloss.IndexOfAny(ItemSeparators);
        var first = end < 0 ? gloss : gloss[..end];
        return new string(first.Where(c => c is >= '一' and <= '鿿').ToArray());
    }

    private static bool IsCovered(string item, string lemmaTranslation)
    {
        if (lemmaTranslation.Contains(item, StringComparison.Ordinal)) return true;
        if (item.Length < 3) return false;

        for (var i = 0; i + 2 <= item.Length; i++)
        {
            if (lemmaTranslation.Contains(item.Substring(i, 2), StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
