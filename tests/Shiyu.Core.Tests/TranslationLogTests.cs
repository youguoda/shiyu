using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>翻译记录（用户需求 2026-10-09）：与剪贴板条目分开的一张表。</summary>
public class TranslationLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Records_come_back_newest_first_with_where_and_which_template()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        store.LogTranslation("你好", "Hello", TranslationOrigin.ReverseInput, "口语", Noon);
        store.LogTranslation("rollout", "发布", TranslationOrigin.Panel, "标准", Noon.AddMinutes(1));

        var log = store.TranslationLog();
        Assert.Equal(["发布", "Hello"], log.Select(record => record.Translated));
        Assert.Equal(TranslationOrigin.Panel, log[0].Origin);
        Assert.Equal("标准", log[0].Template);
        Assert.Equal(TranslationOrigin.ReverseInput, log[1].Origin);
        Assert.Equal(Noon, log[1].CreatedAt);
        Assert.Equal(2, store.CountTranslationLog());
    }

    [Fact]
    public void The_same_result_handed_in_again_is_not_logged_twice()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        Assert.True(store.LogTranslation("你好", "Hello", TranslationOrigin.Panel, "标准", Noon));
        Assert.False(store.LogTranslation("你好", "Hello", TranslationOrigin.ReverseInput, "标准", Noon.AddSeconds(5)));
        Assert.True(store.LogTranslation("你好", "Hi there", TranslationOrigin.Panel, "口语", Noon.AddSeconds(9)));

        Assert.Equal(2, store.CountTranslationLog());
    }

    [Fact]
    public void Blank_text_is_never_logged()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        Assert.False(store.LogTranslation("  ", "Hello", TranslationOrigin.Panel, "标准", Noon));
        Assert.False(store.LogTranslation("你好", "", TranslationOrigin.Panel, "标准", Noon));
        Assert.Equal(0, store.CountTranslationLog());
    }

    [Fact]
    public void Search_finds_either_side_and_reads_wildcards_literally()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        store.LogTranslation("增量发布", "incremental rollout", TranslationOrigin.Panel, "标准", Noon);
        store.LogTranslation("百分之百", "100%", TranslationOrigin.Panel, "标准", Noon.AddMinutes(1));
        store.LogTranslation("你好", "Hello", TranslationOrigin.ReverseInput, "口语", Noon.AddMinutes(2));

        Assert.Single(store.TranslationLog("发布"));
        Assert.Single(store.TranslationLog("rollout"));
        Assert.Single(store.TranslationLog("%"));
        Assert.Equal(3, store.TranslationLog("  ").Count);
    }

    [Fact]
    public void Delete_clear_and_purge_each_do_exactly_their_part()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        store.LogTranslation("一", "one", TranslationOrigin.Panel, "标准", Noon.AddDays(-40));
        store.LogTranslation("二", "two", TranslationOrigin.Panel, "标准", Noon.AddDays(-10));
        store.LogTranslation("三", "three", TranslationOrigin.Panel, "标准", Noon);

        Assert.Equal(1, store.PurgeTranslationLog(Noon.AddDays(-30)));
        Assert.Equal(["three", "two"], store.TranslationLog().Select(record => record.Translated));

        var two = store.TranslationLog("two").Single();
        Assert.True(store.DeleteTranslationRecord(two.Id));
        Assert.False(store.DeleteTranslationRecord(two.Id));

        Assert.Equal(1, store.ClearTranslationLog());
        Assert.Equal(0, store.CountTranslationLog());
    }

    [Fact]
    public void The_log_never_touches_entries_nor_wakes_the_bar()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.Append("剪贴板里的一条", "probe", Noon);

        var entryChanges = 0;
        var logChanges = 0;
        store.Changed += () => entryChanges++;
        store.TranslationLogChanged += () => logChanges++;

        store.LogTranslation("你好", "Hello", TranslationOrigin.Panel, "标准", Noon);
        store.PurgeTranslationLog(Noon.AddDays(1));
        store.ClearTranslationLog();

        Assert.Equal(0, entryChanges);
        Assert.Equal(2, logChanges);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void A_database_reopened_keeps_its_log()
    {
        using var database = new TempDatabase();
        using (var first = EntryStore.Open(database.FilePath))
        {
            first.LogTranslation("你好", "Hello", TranslationOrigin.Panel, "标准", Noon);
        }

        using var second = EntryStore.Open(database.FilePath);
        Assert.Equal(1, second.CountTranslationLog());
    }

    [Fact]
    public void Undoing_a_delete_puts_the_records_back_where_they_were()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.LogTranslation("一", "one", TranslationOrigin.Panel, "标准", Noon.AddMinutes(-2));
        store.LogTranslation("二", "two", TranslationOrigin.ReverseInput, "口语", Noon.AddMinutes(-1));
        store.LogTranslation("三", "three", TranslationOrigin.Panel, "标准", Noon);

        var before = store.TranslationLog();
        var gone = before.Where(record => record.Translated is "one" or "two").ToList();
        foreach (var record in gone)
        {
            store.DeleteTranslationRecord(record.Id);
        }

        // 新记一条（拿到新号），再撤销：放回的两条不会撞上它，也不会多出来。
        store.LogTranslation("四", "four", TranslationOrigin.Panel, "标准", Noon.AddMinutes(1));
        Assert.Equal(2, store.RestoreTranslationRecords(gone));
        Assert.Equal(0, store.RestoreTranslationRecords(gone));

        Assert.Equal(["four", "three", "two", "one"], store.TranslationLog().Select(record => record.Translated));
        Assert.Equal(before.Single(record => record.Translated == "two"), store.TranslationLog("two").Single());
    }
}

public class TranslationLoggerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_translation_touching_an_excluded_pattern_is_not_logged_on_either_side()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var policy = new ExclusionPolicy([ExclusionRule.ForContentPattern("sekrit")]);
        var logger = new TranslationLogger(store, () => new AppSettings(), () => policy, new TestClock(Noon));

        Assert.False(logger.Log("my sekrit plan", "我的计划", TranslationOrigin.Panel, "标准"));
        Assert.False(logger.Log("我的秘密", "my sekrit", TranslationOrigin.ReverseInput, "标准"));
        Assert.True(logger.Log("你好", "Hello", TranslationOrigin.ReverseInput, "口语"));

        var record = Assert.Single(store.TranslationLog());
        Assert.Equal(Noon, record.CreatedAt);
        Assert.Empty(store.Recent(limit: 10));
    }

    [Fact]
    public void Switched_off_it_logs_nothing_and_reads_the_switch_each_time()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var settings = new AppSettings { TranslationLogEnabled = false };
        var logger = new TranslationLogger(store, () => settings, () => new ExclusionPolicy(), new TestClock(Noon));

        Assert.False(logger.Log("你好", "Hello", TranslationOrigin.Panel, "标准"));

        settings = settings with { TranslationLogEnabled = true };
        Assert.True(logger.Log("你好", "Hello", TranslationOrigin.Panel, "标准"));
    }

    [Fact]
    public void A_selection_with_a_trailing_newline_is_the_same_line_as_without()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var logger = new TranslationLogger(store, () => new AppSettings(), () => new ExclusionPolicy(), new TestClock(Noon));

        Assert.True(logger.Log("rollout\r\n", " 发布\n", TranslationOrigin.Panel, "标准"));
        Assert.False(logger.Log("rollout", "发布", TranslationOrigin.Panel, "标准"));

        var record = Assert.Single(store.TranslationLog());
        Assert.Equal("rollout", record.Original);
        Assert.Equal("发布", record.Translated);
    }

    [Fact]
    public void The_sweep_keeps_what_the_chosen_period_covers_and_never_means_never()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.LogTranslation("旧", "old", TranslationOrigin.Panel, "标准", Noon.AddDays(-8));
        store.LogTranslation("新", "new", TranslationOrigin.Panel, "标准", Noon.AddDays(-6));

        var settings = new AppSettings { TranslationLogRetentionDays = 0 };
        var logger = new TranslationLogger(store, () => settings, () => new ExclusionPolicy(), new TestClock(Noon));

        Assert.Equal(0, logger.Sweep());

        settings = settings with { TranslationLogRetentionDays = 7 };
        Assert.Equal(1, logger.Sweep());
        Assert.Equal(["new"], store.TranslationLog().Select(record => record.Translated));
    }
}

public class TranslationLogRetentionTests
{
    [Theory]
    [InlineData(7, 0)]
    [InlineData(30, 1)]
    [InlineData(90, 2)]
    [InlineData(0, 3)]
    public void Each_stored_choice_rests_on_its_own_segment(int days, int choice)
    {
        Assert.Equal(choice, TranslationLogRetention.ChoiceOf(days));
        Assert.Equal(days, TranslationLogRetention.DaysAt(choice));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(14, 1)]
    [InlineData(31, 2)]
    [InlineData(120, 3)]
    [InlineData(-5, 3)]
    public void An_off_scale_value_rounds_toward_keeping_longer(int days, int choice)
        => Assert.Equal(choice, TranslationLogRetention.ChoiceOf(days));

    [Fact]
    public void The_cutoff_follows_what_the_page_shows()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddDays(-30), TranslationLogRetention.CutoffAt(30, now));
        Assert.Equal(now.AddDays(-30), TranslationLogRetention.CutoffAt(14, now));
        Assert.Null(TranslationLogRetention.CutoffAt(0, now));
        Assert.Null(TranslationLogRetention.CutoffAt(365, now));
    }

    [Fact]
    public void An_out_of_range_segment_falls_back_to_the_default()
        => Assert.Equal(30, TranslationLogRetention.DaysAt(9));

    [Fact]
    public void The_log_is_on_and_kept_a_month_by_default_and_round_trips()
    {
        Assert.True(AppSettings.TryParse("{}", out var fresh));
        Assert.True(fresh.TranslationLogEnabled);
        Assert.Equal(30, fresh.TranslationLogRetentionDays);

        Assert.True(AppSettings.TryParse(
            System.Text.Json.JsonSerializer.Serialize(
                new AppSettings { TranslationLogEnabled = false, TranslationLogRetentionDays = 7 }),
            out var changed));
        Assert.False(changed.TranslationLogEnabled);
        Assert.Equal(7, changed.TranslationLogRetentionDays);
    }
}

public class TranslationLogTextTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_meta_line_reads_when_where_and_which_template()
    {
        var record = new TranslationRecord(1, "你好", "Hello", TranslationOrigin.ReverseInput, "口语", Noon.AddMinutes(-3));

        Assert.Equal("3 分钟前  ·  反向输入框  ·  口语", TranslationLogText.Meta(record, Noon, TimeZoneInfo.Utc));
        Assert.Equal("翻译框", TranslationLogText.OriginName(TranslationOrigin.Panel));
    }

    [Fact]
    public void The_original_is_one_line_and_never_runs_on()
    {
        Assert.Equal("第一行 第二行 末尾", TranslationLogText.OneLine("第一行\r\n第二行\n\t末尾  "));

        var long_ = TranslationLogText.OneLine(new string('字', 400));
        Assert.Equal(301, long_.Length);
        Assert.EndsWith("…", long_);
    }

    [Fact]
    public void An_empty_list_says_why()
    {
        Assert.Null(TranslationLogText.Empty(shown: 3, searching: false, enabled: true));
        Assert.True(TranslationLogText.Empty(0, searching: true, enabled: true)!.OfferClear);
        Assert.Equal("还没有翻译记录", TranslationLogText.Empty(0, searching: false, enabled: true)!.Headline);
        Assert.Equal("翻译记录已关闭", TranslationLogText.Empty(0, searching: false, enabled: false)!.Headline);
    }
}
