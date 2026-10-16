using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Shiyu.Core;

/// <summary>
/// 离线英汉词典（评审 2026-10-16 F-08）：随程序发布的 ECDICT 常用词子集（约 4.2 万词与 3 万条词形，
/// 生成见 tools/dictionary）。中文释义、音标、考试标签，查词零延迟、断网可用。
///
/// 只读打开，每次查词一条连接（连接池让它几乎不花钱），线程安全。文件不在、坏了、打不开，都只是 null——
/// 与别的词典端口一样，"没有卡"是唯一允许的失败形态。
/// </summary>
public sealed partial class LocalDictionary(string path) : IDictionaryApi
{
    private static readonly string[] TagOrder = ["zk", "gk", "cet4", "cet6", "ky", "toefl", "ielts", "gre"];

    private static readonly Dictionary<string, string> TagNames = new(StringComparer.Ordinal)
    {
        ["zk"] = "中考",
        ["gk"] = "高考",
        ["cet4"] = "四级",
        ["cet6"] = "六级",
        ["ky"] = "考研",
        ["toefl"] = "托福",
        ["ielts"] = "雅思",
        ["gre"] = "GRE",
    };

    public Task<DictionaryCard?> LookupAsync(string word, CancellationToken cancellation = default)
        => Task.FromResult(Lookup(word));

    public DictionaryCard? Lookup(string word)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            connection.Open();

            foreach (var key in Keys(word))
            {
                if (Headword(connection, key) is { } card)
                {
                    return card;
                }

                if (Lemma(connection, key) is { } lemma && Headword(connection, lemma) is { } inflected)
                {
                    return inflected with { InflectionOf = key };
                }
            }

            return null;
        }
        catch (SqliteException failure)
        {
            // 文件坏了或被别的东西占着：留一行日志，照契约给 null。
            Log.Event(LogEvent.DictionaryLookupFailed, failure, ("leg", 3));
            return null;
        }
    }

    /// <summary>
    /// 查词的键：去空白、小写、弯撇号换直撇号、各种短横换成 -、去掉首尾的标点；以 's 结尾的再试一次不带它的
    /// （dog's → dog）。
    /// </summary>
    internal static IReadOnlyList<string> Keys(string word)
    {
        var text = new StringBuilder(word.Trim().ToLowerInvariant());
        text.Replace('’', '\'').Replace('‘', '\'').Replace('ʼ', '\'');
        foreach (var dash in "‐‑‒–—")
        {
            text.Replace(dash, '-');
        }

        var key = TrimPunctuation(text.ToString());
        if (key.Length == 0)
        {
            return [];
        }

        return key.EndsWith("'s", StringComparison.Ordinal) && key.Length > 2
            ? [key, key[..^2]]
            : [key];
    }

    private static string TrimPunctuation(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && !char.IsLetter(text[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetter(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    private static DictionaryCard? Headword(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT word, phonetic, translation, tag FROM words WHERE word = $key;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var phonetic = reader.IsDBNull(1) ? null : reader.GetString(1);
        var tag = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new DictionaryCard(reader.GetString(0), Ipa(phonetic), Senses(reader.GetString(2)))
        {
            Tags = Tags(tag),
            Offline = true,
        };
    }

    private static string? Lemma(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT lemma FROM forms WHERE form = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 每行一个义项：「n. 例子, 样本」拆成词性与释义；「[医] …」这类领域标注行拿领域当标签（卡上那一格
    /// 本来写词性，空着会白占一行），排在通用义项后面——卡只放得下 <see cref="DictionaryCard.MaxSenses"/> 条，
    /// 先留大家都用得着的。
    /// </summary>
    internal static IReadOnlyList<DictionarySense> Senses(string translation)
    {
        var lines = translation
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return
        [
            .. lines.Where(line => !line.StartsWith('['))
                .Concat(lines.Where(line => line.StartsWith('[')))
                .Take(DictionaryCard.MaxSenses)
                .Select(line => (PartOfSpeech().Match(line) is { Success: true } pos ? pos : Domain().Match(line)) is
                    { Success: true } match
                    ? new DictionarySense(match.Groups["label"].Value, [match.Groups["text"].Value], [], [])
                    : new DictionarySense(null, [line], [], [])),
        ];
    }

    [GeneratedRegex(@"^(?<label>(?:[a-z]+\.)+)\s*(?<text>\S.*)$")]
    private static partial Regex PartOfSpeech();

    [GeneratedRegex(@"^(?<label>\[[^\]]+\])\s*(?<text>\S.*)$")]
    private static partial Regex Domain();

    /// <summary>
    /// ECDICT 的音标写法换成 IPA 的样子：' 主重音 → ˈ，, 次重音 → ˌ，: 长音 → ː，西里尔字母 ә → ə，
    /// ^ → ɡ；外面加 / /，与在线词典的卡一个样子。
    /// </summary>
    internal static string? Ipa(string? phonetic)
    {
        if (string.IsNullOrWhiteSpace(phonetic))
        {
            return null;
        }

        var ipa = new StringBuilder(phonetic.Trim())
            .Replace('\'', 'ˈ')
            .Replace(',', 'ˌ')
            .Replace(':', 'ː')
            .Replace('ә', 'ə')
            .Replace('^', 'ɡ')
            .Replace(";", "; ")
            .ToString();
        return $"/{ipa}/";
    }

    internal static IReadOnlyList<string> Tags(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return [];
        }

        var present = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return [.. TagOrder.Where(present.Contains).Select(code => TagNames[code])];
    }
}

/// <summary>
/// 英文词先查本地（评审 2026-10-16 F-08）：查到就是零延迟的那张卡；查不到再走原来的路——免费词典快路、
/// 大模型兜底。本地卡上没有例句，由在线词典后补（<see cref="EnrichAsync"/>）：它在本机网络常常根本不通，
/// 所以只做锦上添花，绝不挡在本地卡前面。
/// </summary>
public sealed class LocalFirstDictionary(
    LocalDictionary local,
    IDictionaryApi? fallback,
    Func<IDictionaryApi?>? examples) : IDictionaryApi, IDictionaryEnricher, IDisposable
{
    public async Task<DictionaryCard?> LookupAsync(string word, CancellationToken cancellation = default)
        => local.Lookup(word) ?? (fallback is null ? null : await fallback.LookupAsync(word, cancellation));

    /// <summary>
    /// 本地卡补上例句：问在线词典同一个词，按词性对上（noun ↔ n.，verb ↔ v. / vt. / vi.……）挂到对应的
    /// 义项下，每条最多 <see cref="DictionaryCard.MaxExamplesPerSense"/> 句；一条都对不上就挂在第一个义项。
    /// 补的那一趟自己建、自己放在线端口。没有例句可补时为 null。
    /// </summary>
    public async Task<DictionaryCard?> EnrichAsync(DictionaryCard card, CancellationToken cancellation = default)
    {
        if (!card.Offline || examples?.Invoke() is not { } online)
        {
            return null;
        }

        try
        {
            if (await online.LookupAsync(card.Word, cancellation) is not { } fetched)
            {
                return null;
            }

            return WithExamples(card, fetched);
        }
        catch (Exception failure)
        {
            // 契约：补不上就是没有例句，绝不抛；原因留在日志里。
            Log.Event(LogEvent.DictionaryLookupFailed, failure, ("leg", 4));
            return null;
        }
        finally
        {
            (online as IDisposable)?.Dispose();
        }
    }

    internal static DictionaryCard? WithExamples(DictionaryCard card, DictionaryCard fetched)
    {
        var pool = fetched.Senses
            .SelectMany(sense => sense.Examples.Select(example => (Pos: Family(sense.PartOfSpeech), Example: example)))
            .Where(pair => pair.Example.Trim().Length > 0)
            .ToList();
        if (pool.Count == 0 || card.Senses.Count == 0)
        {
            return null;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var senses = card.Senses
            .Select(sense =>
            {
                var family = Family(sense.PartOfSpeech);
                var mine = pool
                    .Where(pair => family is not null && pair.Pos == family && !used.Contains(pair.Example))
                    .Select(pair => pair.Example)
                    .Take(DictionaryCard.MaxExamplesPerSense)
                    .ToList();
                used.UnionWith(mine);
                return mine.Count > 0 ? sense with { Examples = mine } : sense;
            })
            .ToList();

        if (used.Count == 0)
        {
            senses[0] = senses[0] with
            {
                Examples = [.. pool.Select(pair => pair.Example).Take(DictionaryCard.MaxExamplesPerSense)],
            };
        }

        return card with { Senses = senses };
    }

    /// <summary>两边的词性叫法对齐到同一个族：在线词典说 noun，本地说 n.；verb 对 v. / vt. / vi.。</summary>
    private static string? Family(string? partOfSpeech) => partOfSpeech?.Trim().ToLowerInvariant() switch
    {
        "noun" or "n." or "pl." => "noun",
        "verb" or "v." or "vt." or "vi." or "aux." => "verb",
        "adjective" or "a." or "adj." => "adjective",
        "adverb" or "adv." => "adverb",
        "pronoun" or "pron." => "pronoun",
        "preposition" or "prep." => "preposition",
        "conjunction" or "conj." => "conjunction",
        "interjection" or "interj." or "int." => "interjection",
        _ => null,
    };

    public void Dispose() => (fallback as IDisposable)?.Dispose();
}
