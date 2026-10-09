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
    DateTimeOffset CreatedAt);

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
    public IReadOnlyList<TranslationRecord> TranslationLog(string? query = null, int limit = 1000)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var filtered = !string.IsNullOrWhiteSpace(query);
            var where = filtered
                ? "WHERE original LIKE $pattern ESCAPE '\\' OR translated LIKE $pattern ESCAPE '\\'"
                : string.Empty;
            command.CommandText = $"""
                SELECT id, original, translated, origin, template, created_at
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
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
            }

            return records;
        }
    }

    public int CountTranslationLog()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM translation_log;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
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
                    INSERT OR IGNORE INTO translation_log (id, original, translated, origin, template, created_at)
                    VALUES ($id, $original, $translated, $origin, $template, $at);
                    """;
                insert.Parameters.AddWithValue("$id", record.Id);
                insert.Parameters.AddWithValue("$original", record.Original);
                insert.Parameters.AddWithValue("$translated", record.Translated);
                insert.Parameters.AddWithValue("$origin", record.Origin.ToString());
                insert.Parameters.AddWithValue("$template", record.Template);
                insert.Parameters.AddWithValue("$at", record.CreatedAt.ToUnixTimeMilliseconds());
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

    /// <summary>清空全部翻译记录（翻译记录页的「清空全部」）。条目一条不碰。</summary>
    public int ClearTranslationLog() => DeleteTranslationLogWhere(null);

    /// <summary>到期清理：删掉早于 <paramref name="cutoff"/> 的翻译记录。</summary>
    public int PurgeTranslationLog(DateTimeOffset cutoff) => DeleteTranslationLogWhere(cutoff);

    private int DeleteTranslationLogWhere(DateTimeOffset? olderThan)
    {
        int removed;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            if (olderThan is { } cutoff)
            {
                command.CommandText = "DELETE FROM translation_log WHERE created_at < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            }
            else
            {
                command.CommandText = "DELETE FROM translation_log;";
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

    /// <summary>到期清理：按设置的周期删掉旧记录，返回删了几条；「永不」时什么也不做。</summary>
    public int Sweep()
        => TranslationLogRetention.CutoffAt(settings().TranslationLogRetentionDays, clock.GetUtcNow()) is { } cutoff
            ? store.PurgeTranslationLog(cutoff)
            : 0;
}
