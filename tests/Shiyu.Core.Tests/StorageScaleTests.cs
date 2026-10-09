using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// Ticket 12 (O-22): storage designed for a history measured in the hundreds
/// of thousands. The S items — ordering index, subtype sentinel, count cache —
/// each change something observable at the storage layer, so each is pinned
/// here at that layer rather than through a window.
/// </summary>
public class StorageScaleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // --- the ordering index ---------------------------------------------------

    [Fact]
    public void The_ordering_index_exists_after_an_upgrade_from_version_11()
    {
        using var database = new TempDatabase();

        // Written by the current build, then stamped back to 11: a database
        // the previous release left behind, without hand-building its schema.
        using (var store = EntryStore.Open(database.FilePath))
        {
            store.Append("written before the upgrade", "test", Noon);
            using var stamp = store.Connection.CreateCommand();
            stamp.CommandText = "PRAGMA user_version = 11;";
            stamp.ExecuteNonQuery();
        }

        using var upgraded = EntryStore.Open(database.FilePath);

        using var check = upgraded.Connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_entries_order';";
        Assert.Equal(1L, check.ExecuteScalar());
    }

    // --- the subtype sentinel ---------------------------------------------------

    [Fact]
    public void Plain_text_stores_a_sentinel_rather_than_null()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        store.Append("普通文本", "test", Noon);
        store.Append("https://example.com", "test", Noon.AddSeconds(1));
        store.AppendTranslation("翻译过的句子", "Shiyu", Noon.AddSeconds(2), null);
        store.AppendMany([new NewEntry("批量写入", "test", Noon.AddSeconds(3))]);

        // Every write path files a classified row; NULL is a state only older
        // builds could leave, and only the migration touches those.
        using var command = store.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entries WHERE sub_type IS NULL AND kind = 0;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Rows_left_null_by_an_older_build_are_backfilled_once()
    {
        using var database = new TempDatabase();
        var afterUpgrade = (object?)null;

        using (var store = EntryStore.Open(database.FilePath))
        {
            // Written the way an older build would have: no subtype, and a
            // database version to match.
            using var insert = store.Connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO entries (text, source_app, created_at, kind)
                VALUES ('https://legacy.example.com', 'old', 0, 0),
                       ('很普通的一段文字', 'old', 1, 0);
                """;
            insert.ExecuteNonQuery();

            using var stamp = store.Connection.CreateCommand();
            stamp.CommandText = "PRAGMA user_version = 11;";
            stamp.ExecuteNonQuery();
        }

        using (var upgraded = EntryStore.Open(database.FilePath))
        {
            // The classified row keeps its subtype, the plain row its sentinel
            // — one pass, and nothing text-shaped is left NULL.
            Assert.Equal(EntrySubtype.Link, upgraded.Page(10).First(e => e.Text.StartsWith("https")).Subtype);
            Assert.Equal(EntrySubtype.None, upgraded.Page(10).First(e => !e.Text.StartsWith("https")).Subtype);

            using var command = upgraded.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM entries WHERE sub_type IS NULL AND kind = 0;";
            Assert.Equal(0L, command.ExecuteScalar());

            command.CommandText = "PRAGMA user_version;";
            afterUpgrade = command.ExecuteScalar();
        }

        // And never again: the second open has nothing to migrate and the
        // version stays put, which is what retired the startup rescan.
        using (var reopened = EntryStore.Open(database.FilePath))
        {
            using var command = reopened.Connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(afterUpgrade, command.ExecuteScalar());
        }
    }

    // --- the count cache ---------------------------------------------------

    [Fact]
    public void Count_tracks_writes_through_the_cache()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        Assert.Equal(0, store.Count());

        var first = store.Append("one", "test", Noon);
        store.AppendImage("图片 10×10", [1], "unused.png", "test", Noon.AddSeconds(1));
        store.AppendFiles(["C:\\a.txt"], "test", Noon.AddSeconds(2));
        store.AppendTranslation("译文", "Shiyu", Noon.AddSeconds(3), null);
        store.AppendMany([new NewEntry("bulk", "test", Noon.AddSeconds(4))]);
        store.ImportEntry(new Entry(0, "imported", "test", Noon.AddSeconds(5)), groupName: null);

        Assert.Equal(6, store.Count());

        store.Delete(first.Id);
        Assert.Equal(5, store.Count());

        store.DeleteAll();
        Assert.Equal(0, store.Count());

        store.ImportEntry(new Entry(0, "restored", "test", Noon.AddSeconds(6)), groupName: null);
        Assert.Equal(1, store.Count());
    }

    // --- the payload side table (O-22, ticket 12 M) ---------------------------

    /// <summary>
    /// A hand-built version-12 library: the old layout with thumbnail/html/rtf
    /// inline in the entries table. The shape an upgrade actually meets.
    /// </summary>
    private static void CreateVersion12Library(string path, byte[] thumbnail, string html)
    {
        using var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        old.Open();
        using var create = old.CreateCommand();
        create.CommandText = """
            CREATE TABLE entries (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                text        TEXT    NOT NULL,
                source_app  TEXT    NULL,
                created_at  INTEGER NOT NULL,
                kind        INTEGER NOT NULL DEFAULT 0,
                thumbnail   BLOB    NULL,
                original_path TEXT  NULL,
                pinned      INTEGER NOT NULL DEFAULT 0,
                sub_type    TEXT    NULL,
                html        TEXT    NULL,
                rtf         TEXT    NULL,
                files       TEXT    NULL,
                favorite    INTEGER NOT NULL DEFAULT 0,
                note        TEXT    NULL,
                use_count   INTEGER NOT NULL DEFAULT 0,
                group_id    INTEGER NULL,
                translated_from INTEGER NULL,
                image_width INTEGER NOT NULL DEFAULT 0,
                image_height INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE tags (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );
            CREATE TABLE entry_tags (
                entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                tag_id   INTEGER NOT NULL REFERENCES tags(id)    ON DELETE CASCADE,
                PRIMARY KEY (entry_id, tag_id)
            );
            CREATE INDEX idx_entry_tags_tag ON entry_tags (tag_id);
            CREATE TABLE groups (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                name     TEXT    NOT NULL,
                icon     TEXT    NULL,
                position INTEGER NOT NULL DEFAULT 0,
                hidden   INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX idx_entries_created_at ON entries (created_at DESC);
            PRAGMA user_version = 12;

            INSERT INTO entries (text, source_app, created_at, kind, thumbnail, original_path, sub_type, html, image_width, image_height)
            VALUES ('图片 32×32', 'brush', 1758542400000, 1, $png, 'C:\\gone.png', 'None', NULL, 32, 32);
            INSERT INTO entries (text, source_app, created_at, sub_type, html)
            VALUES ('富文本', 'word', 1758542400001, 'None', $html);
            INSERT INTO entries (text, source_app, created_at, sub_type)
            VALUES ('普通文本', 'notepad', 1758542400002, 'None');
            """;
        create.Parameters.AddWithValue("$png", thumbnail);
        create.Parameters.AddWithValue("$html", html);
        create.ExecuteNonQuery();
    }

    [Fact]
    public void An_inline_payload_library_upgrades_without_losing_a_byte()
    {
        using var database = new TempDatabase();
        var thumbnail = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var html = "<p>带格式的<b>旧库</b>内容</p>";
        CreateVersion12Library(database.FilePath, thumbnail, html);

        using var store = EntryStore.Open(database.FilePath);

        var rows = store.Page(10).ToDictionary(entry => entry.Text);

        // Payloads travel to the side table and come back through the reads
        // that ask for them; every marker column survives the rewrite.
        Assert.Equal(thumbnail, store.Get(rows["图片 32×32"].Id)!.ThumbnailPng);
        Assert.Equal(html, store.Get(rows["富文本"].Id)!.Html);
        Assert.Null(store.Get(rows["普通文本"].Id)!.Html);
        Assert.Equal(EntryKind.Image, rows["图片 32×32"].Kind);
        Assert.Equal(32, rows["图片 32×32"].ImageWidth);

        // The old columns are gone from the schema, not merely ignored.
        using var columns = store.Connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('entries') WHERE name IN ('thumbnail', 'html', 'rtf');";
        Assert.Equal(0L, columns.ExecuteScalar());

        using var sideRows = store.Connection.CreateCommand();
        sideRows.CommandText = "SELECT COUNT(*) FROM entry_blobs;";
        Assert.Equal(2L, sideRows.ExecuteScalar());

        // And the upgrade is durable: the second open is an ordinary one.
        using (var reopened = EntryStore.Open(database.FilePath))
        {
            Assert.Equal(3, reopened.Count());
            Assert.Equal(html, reopened.Get(rows["富文本"].Id)!.Html);
        }
    }

    [Fact]
    public void A_payload_migration_interrupted_midway_finishes_on_the_next_open()
    {
        using var database = new TempDatabase();
        var thumbnail = new byte[] { 9, 9, 9, 9 };
        CreateVersion12Library(database.FilePath, thumbnail, "<p>half moved</p>");

        // What a killed upgrade leaves behind: the side table exists, the
        // first row's payload has moved and its source columns are clear, the
        // rest still carry theirs inline — and the version says 12.
        using (var interrupted = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.FilePath}"))
        {
            interrupted.Open();
            using var partial = interrupted.CreateCommand();
            partial.CommandText = """
                CREATE TABLE entry_blobs (
                    entry_id  INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
                    thumbnail BLOB NULL,
                    html      TEXT NULL,
                    rtf       TEXT NULL
                );
                INSERT INTO entry_blobs (entry_id, thumbnail, html, rtf)
                SELECT id, thumbnail, html, rtf FROM entries WHERE id = 1;
                UPDATE entries SET thumbnail = NULL, html = NULL, rtf = NULL WHERE id = 1;
                """;
            partial.ExecuteNonQuery();
        }

        using var store = EntryStore.Open(database.FilePath);

        var rows = store.Page(10).ToDictionary(entry => entry.Text);

        // The already-moved row kept its payload, the interrupted ones arrived
        // with theirs — nothing was copied twice and nothing was dropped.
        Assert.Equal(thumbnail, store.Get(rows["图片 32×32"].Id)!.ThumbnailPng);
        Assert.Equal("<p>half moved</p>", store.Get(rows["富文本"].Id)!.Html);
        Assert.Equal(3, store.Count());

        // And the finished upgrade is the finished upgrade: whatever version
        // the schema settled at is what the next open still reports.
        long settled;
        using (var version = store.Connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            settled = (long)version.ExecuteScalar()!;
        }

        using (var reopened = EntryStore.Open(database.FilePath))
        using (var again = reopened.Connection.CreateCommand())
        {
            again.CommandText = "PRAGMA user_version;";
            Assert.Equal(settled, (long)again.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Payloads_are_fetched_for_a_page_by_id_and_by_cursor_for_whole_history_walks()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        var image = store.AppendImage("图片 4×4", [7, 7, 7], "C:\\x.png", "test", Noon);
        var rich = store.Append("富文本", "test", Noon.AddSeconds(1), html: "<p>x</p>");
        store.Append("普通", "test", Noon.AddSeconds(2));

        // The page read: only the ids a page actually shows.
        var blobs = store.BlobsOf([image.Id, rich.Id]);
        Assert.Equal([7, 7, 7], blobs[image.Id].ThumbnailPng);
        Assert.Equal("<p>x</p>", blobs[rich.Id].Html);
        Assert.Null(blobs[rich.Id].ThumbnailPng);

        // The whole-history walk, by id cursor — the export and merge shape.
        var walked = new List<Entry>();
        long after = 0;
        while (true)
        {
            var page = store.EntriesWithBlobsAfter(after, 2);
            if (page.Count == 0)
            {
                break;
            }

            walked.AddRange(page);
            after = page[^1].Id;
        }

        Assert.Equal(3, walked.Count);
        Assert.Equal([7, 7, 7], walked.Single(entry => entry.Kind == EntryKind.Image).ThumbnailPng);
        Assert.Equal("<p>x</p>", walked.Single(entry => entry.Text == "富文本").Html);
        Assert.Equal(image.Id, walked[0].Id);
    }

    // --- the trigram full-text index (O-22, ticket 12 M) ------------------------

    /// <summary>The bundled SQLite must carry what the schema now depends on.</summary>
    [Fact]
    public void The_bundled_sqlite_is_new_enough_for_drop_column_and_trigram()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        using var command = store.Connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var version = command.ExecuteScalar()!.ToString()!.Split('.');

        // DROP COLUMN arrived in 3.35, the trigram tokenizer in 3.34; the
        // migrations above are only sound while this holds.
        Assert.True(int.Parse(version[0]) > 3 || (int.Parse(version[0]) == 3 && int.Parse(version[1]) >= 35),
            $"bundled SQLite is {string.Join(".", version)}");
    }

    [Theory]
    [InlineData("剪贴板", 1)]   // three characters: the trigram path
    [InlineData("剪贴", 1)]     // two: the LIKE fallback over the narrow table
    [InlineData("剪", 1)]       // one: same
    [InlineData("不存在", 0)]
    public void Chinese_substrings_of_every_length_still_match(string query, int expected)
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.Append("这是一段剪贴板历史记录", "test", Noon);
        store.Append("无关的内容", "test", Noon.AddSeconds(1));

        Assert.Equal(expected, store.Search(query, limit: 10).Count);
    }

    [Fact]
    public void English_search_stays_case_insensitive()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.Append("Deployment Checklist", "test", Noon);

        // The trigram tokenizer folds case by default, which is the behaviour
        // the LIKE scan used to provide — pinned here so nobody has to
        // rediscover it via a user report.
        Assert.Single(store.Search("deployment", limit: 10));
        Assert.Single(store.Search("DEPLOYMENT", limit: 10));
        Assert.Single(store.Search("Deployment", limit: 10));
    }

    [Fact]
    public void Fts_syntax_characters_in_a_query_are_taken_literally()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.Append("100% complete", "test", Noon);
        store.Append("a_b", "test", Noon.AddSeconds(1));

        // Wildcards stay literal (the old promise), and — new with MATCH —
        // so do the characters FTS would otherwise read as operators. The
        // query is wrapped as a quoted phrase, so none of it can throw.
        Assert.Single(store.Search("100%", limit: 10));
        Assert.Single(store.Search("a_b", limit: 10));
        Assert.Empty(store.Search("a OR b", limit: 10));
        Assert.Empty(store.Search("(a", limit: 10));
        Assert.Empty(store.Search("a\"b", limit: 10));
    }

    [Fact]
    public void Deleting_an_entry_removes_it_from_the_index()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        var doomed = store.Append("独一无二的内容", "test", Noon);
        store.Append("留着的内容", "test", Noon.AddSeconds(1));

        store.Delete(doomed.Id);

        Assert.Empty(store.Search("独一无二", limit: 10));
        Assert.Single(store.Search("留着", limit: 10));
    }

    [Fact]
    public void Clearing_the_history_empties_the_index_and_import_repopulates_it()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.Append("导入前就有的句子", "test", Noon);

        store.ClearAll();
        Assert.Empty(store.Search("导入前", limit: 10));

        // The import path runs inside one transaction; the index has to move
        // with it, not after it — these entries must be findable immediately.
        store.RunInTransaction(() => store.ImportEntry(
            new Entry(0, "事务里导入的句子", "test", Noon.AddSeconds(1)), groupName: null));
        Assert.Single(store.Search("事务里导入", limit: 10));
    }

    [Fact]
    public void A_rolled_back_import_leaves_no_searchable_trace()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        Assert.Throws<InvalidOperationException>(() => store.RunInTransaction(() =>
        {
            store.Append("回滚掉的句子", "test", Noon);
            throw new InvalidOperationException("boom");
        }));

        // The trigger-based sync rolls back with the rows; a stale index hit
        // would surface as a ghost row in every search from here on.
        Assert.Empty(store.Search("回滚掉", limit: 10));
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void An_older_library_gets_a_trigram_index_on_upgrade()
    {
        using var database = new TempDatabase();

        // A version-13 shape: no FTS table yet.
        CreateVersion12Library(database.FilePath, [1], "x");
        using (var pre = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.FilePath}"))
        {
            pre.Open();
            using var stamp = pre.CreateCommand();
            stamp.CommandText = "PRAGMA user_version = 13;";
            stamp.ExecuteNonQuery();
        }

        using var store = EntryStore.Open(database.FilePath);

        // Rows that predate the index are searchable the moment it exists —
        // the rebuild reads them out of the content table.
        Assert.Single(store.Search("普通文本", limit: 10));
        Assert.Single(store.Search("富文本", limit: 10));
    }

    // --- the Changed event (O-37, ticket 12 M) ---------------------------------

    [Fact]
    public void Each_write_raises_Changed_once_and_reads_raise_nothing()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        var raised = 0;
        store.Changed += () => raised++;

        // Reads are not news.
        _ = store.Count();
        _ = store.Page(10);
        _ = store.Recent(10);
        _ = store.Search("nothing", 10);
        Assert.Equal(0, raised);

        store.Append("one", "test", Noon);
        store.AppendTranslation("译文", "test", Noon.AddSeconds(1), null);
        store.AppendImage("图片", [1], "C:\\x.png", "test", Noon.AddSeconds(2));
        store.AppendFiles(["C:\\a"], "test", Noon.AddSeconds(3));
        store.SetPinned(1, true);
        store.SetFavorite(1, true);
        store.SetNote(1, "note");
        store.BumpUse(1);
        store.Touch(1, Noon.AddSeconds(9));
        store.AddTag(1, "tag");
        store.RemoveTag(1, "tag");
        var group = store.CreateGroup("组");
        store.SetEntryGroup(1, group);
        store.Delete(1);

        Assert.Equal(14, raised);
    }

    [Fact]
    public void A_batch_reports_once_and_a_rolled_back_batch_reports_nothing()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        var raised = 0;
        store.Changed += () => raised++;

        // Three writes inside one transaction are one piece of news: an
        // importer's hundred rows must not reload a subscriber a hundred
        // times, and mid-transaction broadcasts describe states a rollback
        // then un-happens.
        store.RunInTransaction(() =>
        {
            store.ImportEntry(new Entry(0, "甲", "test", Noon), groupName: null);
            store.ImportEntry(new Entry(0, "乙", "test", Noon.AddSeconds(1)), groupName: null);
            store.ImportEntry(new Entry(0, "丙", "test", Noon.AddSeconds(2)), groupName: null);
        });
        Assert.Equal(1, raised);

        Assert.Throws<InvalidOperationException>(() => store.RunInTransaction(() =>
        {
            store.Append("这句不会留下", "test", Noon.AddSeconds(3));
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(1, raised);
        Assert.Equal(3, store.Count());
    }

    [Fact]
    public void A_handler_may_call_back_into_the_store_without_deadlocking()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        // The event is raised outside the gate precisely so this works — a
        // handler reading the store it was told about is the normal shape
        // (the narrow bar reloads on it).
        store.Changed += () => Assert.Equal(1, store.Count());
        store.Append("the only entry", "test", Noon);
    }

    // --- the full upgrade ladder ------------------------------------------------

    /// <summary>
    /// The oldest shape that still had images: version 2, thumbnails inline,
    /// subtypes not yet invented. This is the library an early adopter brings
    /// to the current build — the whole migration ladder runs in one open, and
    /// everything they had must come through it.
    /// </summary>
    [Fact]
    public void A_version_two_library_upgrades_whole_with_search_and_without_rescans()
    {
        using var database = new TempDatabase();
        var thumbnail = new byte[] { 0x89, 0x50, 0x4E, 0x47, 7, 7 };

        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.FilePath}"))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE entries (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    text        TEXT    NOT NULL,
                    source_app  TEXT    NULL,
                    created_at  INTEGER NOT NULL,
                    kind        INTEGER NOT NULL DEFAULT 0,
                    thumbnail   BLOB    NULL,
                    original_path TEXT  NULL
                );
                CREATE TABLE tags (
                    id   INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE
                );
                CREATE TABLE entry_tags (
                    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                    tag_id   INTEGER NOT NULL REFERENCES tags(id)    ON DELETE CASCADE,
                    PRIMARY KEY (entry_id, tag_id)
                );
                CREATE INDEX idx_entry_tags_tag ON entry_tags (tag_id);
                PRAGMA user_version = 2;

                INSERT INTO entries (text, source_app, created_at, kind, thumbnail, original_path)
                VALUES ('https://old.example.com', 'browser', 1758542400000, 0, NULL, NULL);
                INSERT INTO entries (text, source_app, created_at, kind, thumbnail, original_path)
                VALUES ('很普通的一句话', 'notepad', 1758542400001, 0, NULL, NULL);
                INSERT INTO entries (text, source_app, created_at, kind, thumbnail, original_path)
                VALUES ('老图片 320×240', 'brush', 1758542400002, 1, $png, 'C:\\gone-long-ago.png');
                """;
            create.Parameters.AddWithValue("$png", thumbnail);
            create.ExecuteNonQuery();
        }

        using (var store = EntryStore.Open(database.FilePath))
        {
            // Every entry survived, with its kind and its payload.
            Assert.Equal(3, store.Count());
            var rows = store.Page(10).ToDictionary(entry => entry.Text);
            Assert.Equal(EntrySubtype.Link, rows["https://old.example.com"].Subtype);
            Assert.Equal(EntrySubtype.None, rows["很普通的一句话"].Subtype);
            Assert.Equal(thumbnail, store.Get(rows["老图片 320×240"].Id)!.ThumbnailPng);

            // Search reaches rows the index never saw until now.
            Assert.Single(store.Search("老图片", limit: 10));
            Assert.Single(store.Find(new HistoryFilter { Subtype = EntrySubtype.Link }, limit: 10));

            // The ladder stamped everything it owed: no NULL subtypes left on
            // text rows, current version reached.
            using var command = store.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM entries WHERE sub_type IS NULL AND kind = 0;";
            Assert.Equal(0L, command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(15L, command.ExecuteScalar());
        }

        // And the second open is an ordinary one: nothing left to migrate, the
        // history exactly as the first open left it.
        using (var reopened = EntryStore.Open(database.FilePath))
        {
            Assert.Equal(3, reopened.Count());
            Assert.Single(reopened.Search("很普通", limit: 10));
        }
    }
}
