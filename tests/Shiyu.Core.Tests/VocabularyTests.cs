using Microsoft.Data.Sqlite;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 生词本（评审 2026-10-16 F-09）：词典卡上点星收词，记在翻译记录的一列上；「生词」筛选只看它们；
/// 到期清理与清空全部都不动它们；导出给 Anki 直接导入。
/// </summary>
public class VocabularyTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static DictionaryCard Card(string word, string? example = null) => new(
        word,
        "/ˈwɜːd/",
        [
            new DictionarySense("n.", ["例子, 样本"], example is null ? [] : [example], []),
            new DictionarySense("vt.", ["举例"], [], []),
        ])
    {
        Tags = ["高考", "雅思"],
        Offline = true,
    };

    private static TranslationLogger Logger(EntryStore store, AppSettings? settings = null, ExclusionPolicy? policy = null)
        => new(store, () => settings ?? new AppSettings(), () => policy ?? new ExclusionPolicy(), new TestClock(Noon));

    [Fact]
    public void Starring_a_word_marks_its_latest_lookup_and_the_filter_finds_it()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.LogTranslation("example", "例子", TranslationOrigin.Panel, "标准", Noon);
        store.LogTranslation("a whole sentence", "一整句", TranslationOrigin.Panel, "标准", Noon.AddMinutes(1));

        Assert.True(Logger(store).StarWord("example", "例子", "标准", Card("example", "For example, this.")));

        var words = store.TranslationLog(vocabularyOnly: true);
        var record = Assert.Single(words);
        Assert.Equal("example", record.Original);
        Assert.Equal("example", record.Word!.Headword);
        Assert.Equal("/ˈwɜːd/", record.Word.Phonetic);
        Assert.Equal("n. 例子, 样本；vt. 举例", record.Word.Definition);
        Assert.Equal("For example, this.", record.Word.Example);
        Assert.Equal(["高考", "雅思"], record.Word!.Tags);
        Assert.Equal(1, store.CountTranslationLog(vocabularyOnly: true));
        Assert.Equal(2, store.CountTranslationLog());
        Assert.True(Logger(store).IsStarred("example"));
    }

    [Fact]
    public void Three_starred_words_show_up_as_three_in_the_vocabulary()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var logger = Logger(store);
        foreach (var (word, minute) in new[] { ("alpha", 0), ("beta", 1), ("gamma", 2) })
        {
            store.LogTranslation(word, word + "?", TranslationOrigin.Panel, "标准", Noon.AddMinutes(minute));
            logger.StarWord(word, word + "?", "标准", Card(word));
        }

        Assert.Equal(["gamma", "beta", "alpha"], store.TranslationLog(vocabularyOnly: true).Select(record => record.Word!.Headword));
    }

    [Fact]
    public void A_word_can_be_starred_even_with_the_translation_log_switched_off()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var off = new AppSettings { TranslationLogEnabled = false };

        Assert.True(Logger(store, off).StarWord("ubiquitous", "无处不在的", "标准", Card("ubiquitous")));

        var record = Assert.Single(store.TranslationLog());
        Assert.Equal("ubiquitous", record.Word?.Headword);
        Assert.Equal("无处不在的", record.Translated);
    }

    [Fact]
    public void An_exclusion_rule_still_keeps_a_word_out()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var policy = new ExclusionPolicy([new ExclusionRule(ExclusionRuleKind.ContentPattern, "sekrit")]);

        Assert.False(Logger(store, policy: policy).StarWord("sekrit", "秘密", "标准", Card("sekrit")));
        Assert.Equal(0, store.CountTranslationLog());
    }

    [Fact]
    public void Unstarring_takes_the_word_out_but_keeps_the_record()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.LogTranslation("example", "例子", TranslationOrigin.Panel, "标准", Noon);
        var logger = Logger(store);
        logger.StarWord("example", "例子", "标准", Card("example"));

        Assert.Equal(1, logger.UnstarWord("example"));

        Assert.False(logger.IsStarred("example"));
        Assert.Null(Assert.Single(store.TranslationLog()).Word);
    }

    [Fact]
    public void Retention_and_clear_all_leave_the_vocabulary_alone()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.LogTranslation("old word", "旧词", TranslationOrigin.Panel, "标准", Noon.AddDays(-90));
        store.LogTranslation("old sentence", "旧句", TranslationOrigin.Panel, "标准", Noon.AddDays(-89));
        store.LogTranslation("new sentence", "新句", TranslationOrigin.Panel, "标准", Noon);
        Logger(store).StarWord("old word", "旧词", "标准", Card("old word"));

        Assert.Equal(1, store.PurgeTranslationLog(Noon.AddDays(-30)));
        Assert.Equal(2, store.CountTranslationLog());

        Assert.Equal(1, store.ClearTranslationLog());
        Assert.Equal("old word", Assert.Single(store.TranslationLog()).Original);
    }

    [Fact]
    public void Undoing_a_delete_brings_the_star_back_too()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.LogTranslation("example", "例子", TranslationOrigin.Panel, "标准", Noon);
        Logger(store).StarWord("example", "例子", "标准", Card("example"));
        var record = Assert.Single(store.TranslationLog());

        store.DeleteTranslationRecord(record.Id);
        store.RestoreTranslationRecords([record]);

        Assert.Equal("example", Assert.Single(store.TranslationLog(vocabularyOnly: true)).Word?.Headword);
    }

    // ── 表结构：加一列，旧版改回过版本号也起得来 ──

    [Fact]
    public void A_database_from_before_the_vocabulary_gets_the_column()
    {
        using var database = new TempDatabase();
        EntryStore.Open(database.FilePath).Dispose();
        using (var raw = new SqliteConnection($"Data Source={database.FilePath}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "ALTER TABLE translation_log DROP COLUMN vocabulary; PRAGMA user_version = 15;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var store = EntryStore.Open(database.FilePath);
        store.LogTranslation("example", "例子", TranslationOrigin.Panel, "标准", Noon);
        Assert.True(Logger(store).StarWord("example", "例子", "标准", Card("example")));
    }

    [Fact]
    public void An_older_shiyu_resetting_the_version_number_does_not_break_the_next_start()
    {
        using var database = new TempDatabase();
        EntryStore.Open(database.FilePath).Dispose();

        // 旧版拾语打开过新库：列还在，user_version 被它改回了 15。
        using (var raw = new SqliteConnection($"Data Source={database.FilePath}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "PRAGMA user_version = 15;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var store = EntryStore.Open(database.FilePath);
        Assert.Equal(0, store.CountTranslationLog(vocabularyOnly: true));
    }

    // ── 导出给 Anki ──

    [Fact]
    public void The_vocabulary_exports_as_an_anki_ready_csv_one_line_per_word()
    {
        var word = new VocabularyWord("example", "/igˈzæmpl/", "n. 例子, 样本", "For example, this.", ["高考", "雅思"]);
        var records = new[]
        {
            new TranslationRecord(3, "example", "例子", TranslationOrigin.Panel, "标准", Noon) { Word = word },
            new TranslationRecord(2, "a sentence", "一句话", TranslationOrigin.Panel, "标准", Noon),
            new TranslationRecord(1, "Example", "例子", TranslationOrigin.Panel, "标准", Noon.AddDays(-1)) { Word = word },
        };

        var writer = new StringWriter();
        Assert.Equal(1, VocabularyExport.Write(writer, records, ExportFormat.Csv));

        Assert.Equal(
            "#separator:Comma\r\n#html:false\r\n#columns:词头,音标,释义,例句,标签\r\n"
            + "example,/igˈzæmpl/,\"n. 例子, 样本\",\"For example, this.\",高考 雅思\r\n",
            writer.ToString());
    }

    [Fact]
    public void The_vocabulary_exports_as_markdown_too()
    {
        var records = new[]
        {
            new TranslationRecord(1, "example", "例子", TranslationOrigin.Panel, "标准", Noon)
            {
                Word = new VocabularyWord("example", "/igˈzæmpl/", "n. 例子", null, []),
            },
        };

        var writer = new StringWriter();
        VocabularyExport.Write(writer, records, ExportFormat.Markdown);

        Assert.Equal("## example  /igˈzæmpl/\n\nn. 例子\n\n", writer.ToString());
    }
}
