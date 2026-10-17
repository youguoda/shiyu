using Microsoft.Data.Sqlite;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 图里的字进库、进全文索引（评审 2026-10-16 F-07）：复制过的截图，搜其中的词能命中；后台补认只挑还没认过、
/// 原图还在的；旧库升级时全文索引换成两列，旧版改回过版本号的库也起得来。
/// </summary>
public class OcrStorageTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Words_recognised_in_a_screenshot_are_found_by_search()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var image = store.AppendImage("图片 800×600", [9], @"C:\images\a.png", "Snip", Noon);
        store.Append("an unrelated note", "test", Noon.AddMinutes(1));

        store.SetOcrText(image.Id, "发布清单\n启动后 1 小时的内存读数");

        Assert.Equal([image.Id], store.Search("内存读数", limit: 10).Select(entry => entry.Id));
        Assert.Equal([image.Id], store.Search("清单", limit: 10).Select(entry => entry.Id));
        Assert.Equal("发布清单\n启动后 1 小时的内存读数", store.Get(image.Id)!.OcrText);
    }

    [Fact]
    public void Text_entries_cannot_be_given_recognised_text()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var text = store.Append("plain", "test", Noon);

        store.SetOcrText(text.Id, "should not stick");

        Assert.Null(store.Get(text.Id)!.OcrText);
        Assert.Empty(store.Search("not stick", limit: 10));
    }

    [Fact]
    public void Only_unread_images_with_their_original_wait_for_recognition_newest_first()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var older = store.AppendImage("图片 1", [9], @"C:\images\1.png", "Snip", Noon);
        var newer = store.AppendImage("图片 2", [9], @"C:\images\2.png", "Snip", Noon.AddMinutes(1));
        var read = store.AppendImage("图片 3", [9], @"C:\images\3.png", "Snip", Noon.AddMinutes(2));
        var swept = store.AppendImage("图片 4", [9], @"C:\images\4.png", "Snip", Noon.AddMinutes(3));
        store.SetOcrText(read.Id, string.Empty);
        store.ClearOriginal(swept.Id);

        Assert.Equal([newer.Id, older.Id], store.ImagesAwaitingText(10).Select(image => image.Id));
        Assert.Equal([newer.Id], store.ImagesAwaitingText(1).Select(image => image.Id));
    }

    [Fact]
    public void A_version_sixteen_library_gets_a_two_column_index_and_keeps_its_text_search()
    {
        using var database = new TempDatabase();
        using (var first = EntryStore.Open(database.FilePath))
        {
            first.Append("rollout plan for monday", "test", Noon);
            first.AppendImage("图片 800×600", [9], @"C:\images\a.png", "Snip", Noon.AddMinutes(1));
        }

        // 倒回 16 的样子：一列的全文索引、没有 ocr_text 列。
        using (var raw = new SqliteConnection($"Data Source={database.FilePath}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = """
                DROP TRIGGER entries_fts_insert; DROP TRIGGER entries_fts_delete; DROP TRIGGER entries_fts_update;
                DROP TABLE entries_fts;
                ALTER TABLE entries DROP COLUMN ocr_text;
                CREATE VIRTUAL TABLE entries_fts USING fts5(text, tokenize = 'trigram', content = 'entries', content_rowid = 'id');
                CREATE TRIGGER entries_fts_insert AFTER INSERT ON entries BEGIN
                    INSERT INTO entries_fts (rowid, text) VALUES (new.id, new.text);
                END;
                INSERT INTO entries_fts (entries_fts) VALUES ('rebuild');
                PRAGMA user_version = 16;
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var store = EntryStore.Open(database.FilePath);
        Assert.Single(store.Search("rollout", limit: 10));

        var image = store.Recent(limit: 10).Single(entry => entry.Kind == EntryKind.Image);
        store.SetOcrText(image.Id, "截图里的季度目标");
        Assert.Equal([image.Id], store.Search("季度目标", limit: 10).Select(entry => entry.Id));
    }

    [Fact]
    public void An_older_shiyu_resetting_the_version_does_not_rebuild_or_break_anything()
    {
        using var database = new TempDatabase();
        long image;
        using (var first = EntryStore.Open(database.FilePath))
        {
            image = first.AppendImage("图片", [9], @"C:\images\a.png", "Snip", Noon).Id;
            first.SetOcrText(image, "保留下来的字");
        }

        using (var raw = new SqliteConnection($"Data Source={database.FilePath}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "PRAGMA user_version = 16;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var store = EntryStore.Open(database.FilePath);
        Assert.Equal([image], store.Search("保留下来", limit: 10).Select(entry => entry.Id));
    }
}
