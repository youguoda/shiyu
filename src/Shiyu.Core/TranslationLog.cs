namespace Shiyu.Core;

/// <summary>Where a logged translation was made.</summary>
public enum TranslationOrigin
{
    /// <summary>翻译框（划词翻译、翻译剪贴板打开的面板）。</summary>
    Panel,

    /// <summary>反向输入框。</summary>
    ReverseInput,
}

/// <summary>
/// One line of the translation log（翻译记录，用户需求 2026-10-09）: what went in, what came
/// out, where, and with which prompt template.
/// </summary>
public sealed record TranslationRecord(
    long Id,
    string Original,
    string Translated,
    TranslationOrigin Origin,
    string Template,
    DateTimeOffset CreatedAt)
{
    /// <summary>收进了生词本时，那张词典卡的要点；没收为 null（评审 2026-10-16 F-09）。</summary>
    public VocabularyWord? Word { get; init; }
}

/// <summary>
/// 生词本里的一个词（评审 2026-10-16 F-09）：收进来那一刻词典卡的要点，存成翻译记录上的一列 JSON。
/// 存卡的要点而不是只存词：词典以后换了数据，生词本里记着的仍是当时看到的那张。
/// </summary>
/// <param name="Definition">各义项连成一行：「n. 例子, 样本；vt. 作为……的例子」。</param>
/// <param name="Example">卡上的第一句例句；没有为 null。</param>
public sealed record VocabularyWord(
    string Headword,
    string? Phonetic,
    string Definition,
    string? Example,
    IReadOnlyList<string> Tags)
{
    public static VocabularyWord From(DictionaryCard card) => new(
        card.Word,
        card.Phonetic,
        string.Join("；", card.Senses.Select(sense =>
            (sense.PartOfSpeech is { Length: > 0 } pos ? pos + " " : string.Empty) + string.Join("；", sense.Definitions))),
        card.Senses.SelectMany(sense => sense.Examples).FirstOrDefault(),
        card.Tags);

    internal string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);

    internal static VocabularyWord? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<VocabularyWord>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            // expected: 认不出的一列当作没收——不让一行坏数据拖垮整页翻译记录。
            return null;
        }
    }
}

/// <summary>翻译记录页的字：行的元信息、单行原文、空态。管理窗照着显示，不自己拼。</summary>
public static class TranslationLogText
{
    public static string OriginName(TranslationOrigin origin) => origin switch
    {
        TranslationOrigin.ReverseInput => "反向输入框",
        _ => "翻译框",
    };

    /// <summary>元信息行，与剪贴板历史同口径：相对时间 · 哪个框 · 模板。</summary>
    public static string Meta(TranslationRecord record, DateTimeOffset now, TimeZoneInfo? zone = null)
        => $"{RelativeTime.For(record.CreatedAt, now, zone)}  ·  {OriginName(record.Origin)}  ·  {record.Template}";

    /// <summary>原文压成一行（换行、制表都成空格），过长截断——行里只给它一行。</summary>
    public static string OneLine(string text)
    {
        var collapsed = string.Join(' ', text.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.Length > 300 ? collapsed[..300] + "…" : collapsed;
    }

    /// <summary>
    /// 列表空着时说什么；有内容时为 null。搜不到给"清除搜索"（<see cref="EmptyStateCopy.OfferClear"/>）；
    /// 保存关着时说清楚为什么空——用户以为在记、其实没记，是最该被告知的那种空。
    /// </summary>
    public static EmptyStateCopy? Empty(int shown, bool searching, bool enabled)
    {
        if (shown > 0)
        {
            return null;
        }

        if (searching)
        {
            return new EmptyStateCopy("没有找到", "原文和译文里都没有这个词，换个说法试试。", OfferClear: true);
        }

        return enabled
            ? new EmptyStateCopy("还没有翻译记录", "在翻译框或反向输入框里翻译后，原文和译文会记在这里。", OfferClear: false)
            : new EmptyStateCopy("翻译记录已关闭", "打开设置里的「保存翻译记录」，之后的每一次翻译都会记在这里。", OfferClear: false);
    }
}

/// <summary>
/// 翻译记录的自动清空周期：设置页的四档（7 天、30 天、90 天、永不）与存下的天数之间的换算。
/// 显示与执行走同一个换算——页上写着几天，到期就按几天删。
/// </summary>
public static class TranslationLogRetention
{
    /// <summary>分段按钮从左到右的天数；0 是「永不」。</summary>
    public static readonly IReadOnlyList<int> Days = [7, 30, 90, 0];

    private const int Never = 3;

    /// <summary>
    /// 存下的天数落在哪一档。不在档上的值（手改的设置文件）归到"不早于它删"的那一档：
    /// 14 天归 30 天、120 天归永不——宁可多留，不替用户提前删。
    /// </summary>
    public static int ChoiceOf(int days)
    {
        if (days <= 0)
        {
            return Never;
        }

        for (var choice = 0; choice < Never; choice++)
        {
            if (Days[choice] >= days)
            {
                return choice;
            }
        }

        return Never;
    }

    /// <summary>分段第 <paramref name="choice"/> 档存成几天；越界按默认的 30 天。</summary>
    public static int DaysAt(int choice)
        => choice >= 0 && choice < Days.Count ? Days[choice] : new AppSettings().TranslationLogRetentionDays;

    /// <summary>此刻该删掉哪个时间以前的记录；永不清空时为 null。</summary>
    public static DateTimeOffset? CutoffAt(int days, DateTimeOffset now)
        => DaysAt(ChoiceOf(days)) is > 0 and var kept ? now.AddDays(-kept) : null;
}

/// <summary>
/// 翻译记录：翻译框与反向输入框的每一次翻译，存在与剪贴板历史同一个库里、独立的一张表。
/// 它们不是条目——不进窄条、不参与条目的保留与删除，按自己的周期清空
/// （<see cref="AppSettings.TranslationLogRetentionDays"/>）。只在本机。
/// </summary>
public sealed partial class EntryStore
{
    /// <summary>
    /// 翻译记录变了（写入、删除、清空、到期清理）。与 <see cref="Changed"/> 分开：窄条只看
    /// 条目，一条翻译记录不该让它重读。
    /// </summary>
    public event Action? TranslationLogChanged;

    /// <summary>
    /// 记一条翻译。与最新一条的原文、译文都相同时不再记：换模板又换回来、面板重绘、
    /// 反向输入框贴完又关窗，都会把同一个结果再交一次。返回是否真的写了一条。
    /// </summary>
    public bool LogTranslation(
        string original, string translated, TranslationOrigin origin, string template, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        lock (_gate)
        {
            using (var newest = _connection.CreateCommand())
            {
                newest.CommandText = """
                    SELECT original, translated FROM translation_log
                    ORDER BY created_at DESC, id DESC LIMIT 1;
                    """;
                using var reader = newest.ExecuteReader();
                if (reader.Read() && reader.GetString(0) == original && reader.GetString(1) == translated)
                {
                    return false;
                }
            }

            using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO translation_log (original, translated, origin, template, created_at)
                VALUES ($original, $translated, $origin, $template, $at);
                """;
            insert.Parameters.AddWithValue("$original", original);
            insert.Parameters.AddWithValue("$translated", translated);
            insert.Parameters.AddWithValue("$origin", origin.ToString());
            insert.Parameters.AddWithValue("$template", template);
            insert.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
            insert.ExecuteNonQuery();
        }

        TranslationLogChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 最新的在前。<paramref name="query"/> 在原文与译文里按子串找（通配符按字面）。
    /// </summary>
    public IReadOnlyList<TranslationRecord> TranslationLog(string? query = null, int limit = 1000, bool vocabularyOnly = false)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var filtered = !string.IsNullOrWhiteSpace(query);
            var conditions = new List<string>();
            if (filtered)
            {
                conditions.Add("(original LIKE $pattern ESCAPE '\\' OR translated LIKE $pattern ESCAPE '\\')");
            }

            // 「生词」筛选（评审 2026-10-16 F-09）：只看收进了生词本的。
            if (vocabularyOnly)
            {
                conditions.Add("vocabulary IS NOT NULL");
            }

            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
            command.CommandText = $"""
                SELECT id, original, translated, origin, template, created_at, vocabulary
                FROM translation_log
                {where}
                ORDER BY created_at DESC, id DESC
                LIMIT $limit;
                """;
            if (filtered)
            {
                command.Parameters.AddWithValue("$pattern", $"%{EscapeForLike(query!.Trim())}%");
            }

            command.Parameters.AddWithValue("$limit", limit);

            var records = new List<TranslationRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new TranslationRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Enum.TryParse<TranslationOrigin>(reader.GetString(3), out var origin) ? origin : TranslationOrigin.Panel,
                    reader.GetString(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)))
                {
                    Word = VocabularyWord.FromJson(reader.IsDBNull(6) ? null : reader.GetString(6)),
                });
            }

            return records;
        }
    }

    public int CountTranslationLog(bool vocabularyOnly = false)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = vocabularyOnly
                ? "SELECT COUNT(*) FROM translation_log WHERE vocabulary IS NOT NULL;"
                : "SELECT COUNT(*) FROM translation_log;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    // --- 生词本（评审 2026-10-16 F-09）：收进来的词是翻译记录上的一列 ---------------------------------

    /// <summary>这个原文最近的一条翻译记录；没有为 null。词典卡上的星标收的就是它。</summary>
    public long? LatestTranslationId(string original)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id FROM translation_log WHERE original = $original
                ORDER BY created_at DESC, id DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$original", original);
            return command.ExecuteScalar() is long id ? id : null;
        }
    }

    /// <summary>把一条记录收进生词本（<paramref name="word"/> 为 null 是移出）。</summary>
    public bool SetVocabulary(long id, VocabularyWord? word)
    {
        int changed;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE translation_log SET vocabulary = $word WHERE id = $id;";
            command.Parameters.AddWithValue("$word", (object?)word?.ToJson() ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            TranslationLogChanged?.Invoke();
        }

        return changed > 0;
    }

    /// <summary>
    /// 没有现成的记录可收时（翻译记录关着、或还没译完），专为这个词记一条，带着生词本那一列。
    /// 收生词是用户自己点的，不受翻译记录开关管。
    /// </summary>
    public long AddVocabulary(
        string original, string translated, string template, VocabularyWord word, DateTimeOffset at)
    {
        long id;
        lock (_gate)
        {
            using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO translation_log (original, translated, origin, template, created_at, vocabulary)
                VALUES ($original, $translated, $origin, $template, $at, $word)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$original", original);
            insert.Parameters.AddWithValue("$translated", translated);
            insert.Parameters.AddWithValue("$origin", TranslationOrigin.Panel.ToString());
            insert.Parameters.AddWithValue("$template", template);
            insert.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$word", word.ToJson());
            id = (long)insert.ExecuteScalar()!;
        }

        TranslationLogChanged?.Invoke();
        return id;
    }

    /// <summary>这个词头在不在生词本里（词典卡上的星标实心还是空心）。</summary>
    public bool InVocabulary(string headword)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT 1 FROM translation_log
                WHERE vocabulary IS NOT NULL AND json_extract(vocabulary, '$.Headword') = $headword
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$headword", headword);
            return command.ExecuteScalar() is not null;
        }
    }

    /// <summary>把这个词头移出生词本：收过它的每一条都摘掉星标，记录本身留着。</summary>
    public int RemoveFromVocabulary(string headword)
    {
        int changed;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE translation_log SET vocabulary = NULL
                WHERE vocabulary IS NOT NULL AND json_extract(vocabulary, '$.Headword') = $headword;
                """;
            command.Parameters.AddWithValue("$headword", headword);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            TranslationLogChanged?.Invoke();
        }

        return changed;
    }

    public bool DeleteTranslationRecord(long id)
    {
        int removed;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM translation_log WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            removed = command.ExecuteNonQuery();
        }

        if (removed > 0)
        {
            TranslationLogChanged?.Invoke();
        }

        return removed > 0;
    }

    /// <summary>
    /// 撤销删除：把刚删掉的记录原样放回（同 id、同时间，列表里回到原来的位置）。id 由
    /// AUTOINCREMENT 发，删掉的号不会再发给别人，放回原号不会撞车；已经在库里的跳过。
    /// </summary>
    public int RestoreTranslationRecords(IEnumerable<TranslationRecord> records)
    {
        var restored = 0;
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var record in records)
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO translation_log (id, original, translated, origin, template, created_at, vocabulary)
                    VALUES ($id, $original, $translated, $origin, $template, $at, $word);
                    """;
                insert.Parameters.AddWithValue("$id", record.Id);
                insert.Parameters.AddWithValue("$original", record.Original);
                insert.Parameters.AddWithValue("$translated", record.Translated);
                insert.Parameters.AddWithValue("$origin", record.Origin.ToString());
                insert.Parameters.AddWithValue("$template", record.Template);
                insert.Parameters.AddWithValue("$at", record.CreatedAt.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$word", (object?)record.Word?.ToJson() ?? DBNull.Value);
                restored += insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        if (restored > 0)
        {
            TranslationLogChanged?.Invoke();
        }

        return restored;
    }

    /// <summary>
    /// 清空全部翻译记录（翻译记录页的「清空全部」）。条目一条不碰；生词本里的词也留着（评审 2026-10-16
    /// F-09）——那是用户一个个点星收下的，与"清掉翻译的流水"不是一回事，要删就在「生词」里删。
    /// </summary>
    public int ClearTranslationLog() => DeleteTranslationLogWhere(null);

    /// <summary>到期清理：删掉早于 <paramref name="cutoff"/> 的翻译记录；生词本里的不随周期走。</summary>
    public int PurgeTranslationLog(DateTimeOffset cutoff) => DeleteTranslationLogWhere(cutoff);

    private int DeleteTranslationLogWhere(DateTimeOffset? olderThan)
    {
        int removed;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            if (olderThan is { } cutoff)
            {
                command.CommandText = "DELETE FROM translation_log WHERE created_at < $cutoff AND vocabulary IS NULL;";
                command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            }
            else
            {
                command.CommandText = "DELETE FROM translation_log WHERE vocabulary IS NULL;";
            }

            removed = command.ExecuteNonQuery();
        }

        if (removed > 0)
        {
            TranslationLogChanged?.Invoke();
        }

        return removed;
    }
}

/// <summary>
/// 翻译记录的唯一入口：开关、排除闸、时钟与周期在这里汇合，翻译框与反向输入框都从这里记，
/// 到期清理也从这里走。设置与排除名单按调用时取最新的——改了即刻作数，不必重启。
/// 不经剪贴板流水线：探针实例没有流水线，翻译记录却照样要记。
/// </summary>
public sealed class TranslationLogger(
    EntryStore store,
    Func<AppSettings> settings,
    Func<ExclusionPolicy> exclusions,
    TimeProvider clock)
{
    /// <summary>
    /// 记一条。设置关着不记；原文或译文命中排除名单的内容规则，整条不记——排除名单说
    /// "这种东西别存"，翻译记录也是存。来源程序在这里已无从查起，只有内容规则起作用。
    /// 两端的空白不记：划词常带着行尾换行，同一句话不该因此成了两条。
    /// </summary>
    public bool Log(string original, string translated, TranslationOrigin origin, string template)
    {
        if (!settings().TranslationLogEnabled)
        {
            return false;
        }

        var policy = exclusions();
        if (policy.Excludes(new ClipboardSnapshot(original, null, false))
            || policy.Excludes(new ClipboardSnapshot(translated, null, false)))
        {
            return false;
        }

        return store.LogTranslation(original.Trim(), translated.Trim(), origin, template, clock.GetUtcNow());
    }

    /// <summary>
    /// 词典卡上的星标（评审 2026-10-16 F-09）：把这次查的词收进生词本。收的是这个原文最近的一条翻译记录；
    /// 还没有（翻译记录关着、或还没译完）就专为它记一条——收生词是用户自己点的，不受翻译记录开关管。
    /// 排除名单的内容规则照样拦。
    /// </summary>
    public bool StarWord(string original, string translated, string template, DictionaryCard card)
    {
        var key = original.Trim();
        if (key.Length == 0 || exclusions().Excludes(new ClipboardSnapshot(key, null, false)))
        {
            return false;
        }

        var word = VocabularyWord.From(card);
        if (store.LatestTranslationId(key) is { } id)
        {
            return store.SetVocabulary(id, word);
        }

        var said = translated.Trim();
        store.AddVocabulary(key, said.Length > 0 ? said : card.Word, template, word, clock.GetUtcNow());
        return true;
    }

    /// <summary>星标点回空心：这个词头移出生词本，记录本身留着。</summary>
    public int UnstarWord(string headword) => store.RemoveFromVocabulary(headword);

    public bool IsStarred(string headword) => store.InVocabulary(headword);

    /// <summary>到期清理：按设置的周期删掉旧记录，返回删了几条；「永不」时什么也不做。</summary>
    public int Sweep()
        => TranslationLogRetention.CutoffAt(settings().TranslationLogRetentionDays, clock.GetUtcNow()) is { } cutoff
            ? store.PurgeTranslationLog(cutoff)
            : 0;
}
