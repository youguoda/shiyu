using System.Text;
using System.Text.RegularExpressions;

namespace DictionaryBuilder;

/// <summary>
/// exchange 字段里的一项。"p:went" 解析成 (Kind: 'p', Value: "went")。
/// Kind 含义：p 过去式、d 过去分词、i 现在分词、3 第三人称单数、r 比较级、t 最高级、s 复数；
/// 0 表示"本条是某词的变形，原形是 Value"，1 表示变形的类型（值是类型码，不是单词）。
/// </summary>
internal readonly record struct ExchangeItem(char Kind, string Value);

/// <summary>ecdict.csv 的一行（只取用得到的列），文本已经清理过。</summary>
internal sealed class EcdictEntry
{
    /// <summary>词头原样（可能有大写，如 "Polish"）。</summary>
    public required string Word { get; init; }

    /// <summary>查询键：词头转小写。</summary>
    public required string Key { get; init; }

    public required string Phonetic { get; init; }

    /// <summary>中文释义。每个词性一行，行与行之间是真正的 '\n'。</summary>
    public required string Translation { get; init; }

    /// <summary>考试标签，空格分隔，如 "zk gk cet4"。</summary>
    public required string Tag { get; init; }

    public required int Collins { get; init; }
    public required bool Oxford { get; init; }
    public required int Bnc { get; init; }
    public required int Frq { get; init; }
    public required IReadOnlyList<ExchangeItem> Exchange { get; init; }

    /// <summary>词条在 CSV 里的先后次序。用来让"同样好"的选择有确定的结果。</summary>
    public required int Order { get; init; }

    /// <summary>
    /// 词频名次，越小越常用。frq 和 bnc 都是名次（1 = 最常用，0/空 = 没有），取其中较靠前的一个；
    /// 两个都没有时是 int.MaxValue。
    /// </summary>
    public int Rank
    {
        get
        {
            var rank = int.MaxValue;
            if (Frq > 0) rank = Math.Min(rank, Frq);
            if (Bnc > 0) rank = Math.Min(rank, Bnc);
            return rank;
        }
    }
}

/// <summary>一行 CSV 为什么被拒绝（或没被拒绝）。</summary>
internal enum RowVerdict
{
    Ok,
    NotSingleWord,        // 有空格、数字或其它符号，或者首尾是连字符/撇号
    EmptyTranslation,     // translation 为空
    NoChineseTranslation, // translation 非空，但里面一个汉字都没有（多是 "= has not" 这样的互相参见）
}

/// <summary>按表头里的列名取值，这样列的顺序变了也不会读错。</summary>
internal sealed class EcdictColumns
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public EcdictColumns(IReadOnlyList<string> header)
    {
        for (var i = 0; i < header.Count; i++) _index[header[i].Trim()] = i;
        foreach (var name in new[] { "word", "phonetic", "translation", "collins", "oxford", "tag", "bnc", "frq", "exchange" })
        {
            if (!_index.ContainsKey(name))
                throw new InvalidDataException($"ecdict.csv 缺少列 '{name}'，表头是：{string.Join(',', header)}");
        }
        Count = header.Count;
    }

    public int Count { get; }

    public string Get(IReadOnlyList<string> row, string name) => row[_index[name]];
}

internal static partial class EcdictRow
{
    /// <summary>
    /// 单个英文词：字母，内部可以有连字符或撇号（well-known、don't、o'clock），
    /// 不能有空格、数字、点号等其它符号，也不能以连字符/撇号开头或结尾。
    /// ECDICT 里只有 1 个带非 ASCII 字母的单词条目（且不够常用），所以只认 ASCII 字母。
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z]+(?:['\-][A-Za-z]+)*$")]
    private static partial Regex WordShape();

    public static bool IsWordShape(string s) => WordShape().IsMatch(s);

    public static RowVerdict TryParse(IReadOnlyList<string> row, EcdictColumns columns, int order, out EcdictEntry? entry)
    {
        entry = null;

        var word = columns.Get(row, "word").Trim();
        if (!IsWordShape(word)) return RowVerdict.NotSingleWord;

        var translation = TextCleaner.CleanTranslation(columns.Get(row, "translation"));
        if (translation.Length == 0) return RowVerdict.EmptyTranslation;
        if (!TextCleaner.HasChinese(translation)) return RowVerdict.NoChineseTranslation;

        entry = new EcdictEntry
        {
            Word = word,
            Key = word.ToLowerInvariant(),
            Phonetic = TextCleaner.CleanPhonetic(columns.Get(row, "phonetic")),
            Translation = translation,
            Tag = columns.Get(row, "tag").Trim(),
            Collins = ParseInt(columns.Get(row, "collins")),
            Oxford = ParseInt(columns.Get(row, "oxford")) == 1,
            Bnc = ParseInt(columns.Get(row, "bnc")),
            Frq = ParseInt(columns.Get(row, "frq")),
            Exchange = ParseExchange(columns.Get(row, "exchange")),
            Order = order,
        };
        return RowVerdict.Ok;
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    /// <summary>"p:did/d:done/0:go/1:p" -> [(p,did) (d,done) (0,go) (1,p)]，值一律转小写。</summary>
    private static IReadOnlyList<ExchangeItem> ParseExchange(string raw)
    {
        if (raw.Length == 0) return [];
        var items = new List<ExchangeItem>();
        foreach (var part in raw.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 3 || part[1] != ':') continue;
            items.Add(new ExchangeItem(part[0], part[2..].Trim().ToLowerInvariant()));
        }
        return items;
    }
}

/// <summary>ecdict.csv 的文本字段清理。</summary>
internal static class TextCleaner
{
    /// <summary>
    /// ECDICT 的 CSV 里没有真正的换行：换行写成两个字符 "\n"，反斜杠本身写成 "\\"。
    /// 从左到右扫一遍还原，这样 "\\n"（一个反斜杠加字母 n）不会被误当成换行。
    /// </summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                switch (s[i + 1])
                {
                    case 'n':
                    case 'r': // 极少见；当作换行，后面会去掉空行
                        sb.Append('\n');
                        i++;
                        continue;
                    case '\\':
                        sb.Append('\\');
                        i++;
                        continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 还原换行，然后逐行：制表符换成空格、去掉首尾空白（约 4 万行释义以一个多余的空格开头）、丢掉空行。
    /// 行内容本身一个字不改。
    /// </summary>
    public static string CleanTranslation(string raw)
    {
        if (raw.Length == 0) return "";

        var lines = new List<string>();
        foreach (var line in Unescape(raw).Split('\n'))
        {
            var cleaned = line.Replace('\t', ' ').Trim();
            if (cleaned.Length > 0) lines.Add(cleaned);
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 音标：去首尾空白，再去掉个别条目自带的外包符号（"[gi'lespi]"、"/'klæsis/"），
    /// 显示时由 App 统一加括号。音标本身的写法（ECDICT 自己的那套符号）一概不动。
    /// </summary>
    public static string CleanPhonetic(string raw)
    {
        var s = raw.Trim();
        if (s.Length > 0 && s[0] is '[' or '/') s = s[1..];
        if (s.Length > 0 && s[^1] is ']' or '/') s = s[..^1];
        return s.Trim();
    }

    /// <summary>含有至少一个汉字（基本区 U+4E00..U+9FFF）。</summary>
    public static bool HasChinese(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '一' and <= '鿿') return true;
        }
        return false;
    }
}
