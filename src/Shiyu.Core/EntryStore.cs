using Microsoft.Data.Sqlite;

namespace Shiyu.Core;

/// <summary>
/// Where a paged read left off: the sort key of the last row a page returned.
/// The next page asks for everything strictly after it, which reads the same
/// no matter how many rows have been added above it in the meantime — the
/// failure mode OFFSET paging has at depth, where every appended row shifts
/// the window and either repeats or skips history.
/// </summary>
public readonly record struct PageCursor(int Pinned, long CreatedAtMs, long Id)
{
    public static PageCursor Of(Entry entry)
        => new(entry.IsPinned ? 1 : 0, entry.CreatedAt.ToUnixTimeMilliseconds(), entry.Id);
}

/// <summary>
/// The clipboard history, stored in SQLite. Deliberately concrete rather than
/// behind a port: search, filtering and retention are exactly the logic a fake
/// store would stop testing.
/// </summary>
public sealed partial class EntryStore : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>
    /// Every public member runs under this gate. One connection serves the
    /// UI thread and the pool (image recording, retention, backup), and a
    /// connection is not safe to share: a command made on one thread adopts
    /// whatever transaction another thread has open, and last_insert_rowid()
    /// belongs to the connection, not the caller. Monitor is re-entrant, so
    /// members calling members is fine. Nothing public returns a lazy
    /// sequence — a reader that outlived the lock would be unguarded.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>The transaction <see cref="RunInTransaction"/> holds open, if any. Touched only under the gate.</summary>
    private SqliteTransaction? _batch;

    /// <summary>
    /// The last known row count, or null when a write has invalidated it. Read
    /// by <see cref="Count"/> on every narrow-bar paint; a history of a hundred
    /// thousand entries should not be COUNTed that often. Touched only under
    /// the gate, so the invalidation a write performs cannot race the read
    /// another thread is making.
    /// </summary>
    private int? _countCache;

    /// <summary>
    /// Raised once after the outermost write of a call — or of a whole
    /// <see cref="RunInTransaction"/> batch — has committed. The narrow bar
    /// subscribes instead of polling a count every two seconds: a count probe
    /// cannot see a re-copied entry Touch its way back to the top, or a note
    /// edited in another window (O-37).
    ///
    /// The event fires <em>outside</em> the gate, deliberately. A handler that
    /// hops to a UI thread with Dispatcher.Invoke would deadlock against the
    /// gate if it were held (the UI thread may be the one waiting on it), and
    /// handlers are free to call back into the store — every public member
    /// takes the gate cleanly.
    /// </summary>
    public event Action? Changed;

    /// <summary>How many write frames are nested on this thread. Touched only under the gate.</summary>
    private int _writeDepth;

    /// <summary>
    /// Runs one public write. Every write funnels through here so the event
    /// discipline cannot drift: the outermost frame reports, inner frames do
    /// not — an import inside a batch is the batch's to report, and a
    /// mid-transaction broadcast would describe a state a rollback then
    /// un-happens. A write that throws reports nothing, because it landed
    /// nothing.
    /// </summary>
    private TResult Write<TResult>(Func<TResult> work)
    {
        TResult result;
        bool report;

        lock (_gate)
        {
            _writeDepth++;
            try
            {
                result = work();
            }
            finally
            {
                _writeDepth--;
            }

            report = _writeDepth == 0;
        }

        if (report)
        {
            Changed?.Invoke();
        }

        return result;
    }

    private void Write(Action work) => Write<object?>(() =>
    {
        work();
        return null;
    });

    /// <summary>Internal for tests: writing rows the way an older build would have.</summary>
    internal SqliteConnection Connection => _connection;

    private EntryStore(SqliteConnection connection) => _connection = connection;

    public static EntryStore Open(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        var store = new EntryStore(connection);
        store.CreateSchema();
        return store;
    }

    /// <summary>
    /// The shape the code expects. Bumped whenever a migration is added below.
    /// </summary>
    private const int SchemaVersion = 15;

    /// <summary>
    /// The columns every list path reads: everything except the payloads that
    /// live in <c>entry_blobs</c>. Reading a column that physically sits after
    /// a thumbnail or an HTML blob walks that blob's overflow pages first —
    /// the cost the side table exists to remove — so a list query must not
    /// name those columns at all, not even as ones it ignores.
    /// </summary>
    private const string NarrowColumns = """
        id, text, source_app, created_at, kind, original_path, pinned, sub_type,
        files, favorite, note, use_count, group_id, translated_from, image_width, image_height
        """;

    /// <summary>
    /// The entry's tags joined into the eleventh column position — one query
    /// per page rather than one per row.
    /// </summary>
    private const string TagsColumn = """
        (SELECT group_concat(t.name, char(31)) FROM tags t
           JOIN entry_tags et ON et.tag_id = t.id WHERE et.entry_id = entries.id)
        """;

    /// <summary>
    /// What <see cref="EntrySubtype.None"/> is stored as. A value rather than
    /// NULL, so "classified, nothing specific" and "not yet classified" are
    /// different rows: the NULLs are exactly the ones an older build left
    /// behind, and once the migration has filled them, plain text is never
    /// rescanned on startup again (O-22).
    /// </summary>
    private const string SubtypeSentinel = "None";

    /// <summary>
    /// Joins tag names into one column. A unit separator, because it cannot
    /// occur in a tag the user typed — a comma very much can.
    /// </summary>
    private const char TagSeparator = '';

    private void CreateSchema()
    {
        // WAL keeps readers from blocking the writer and leaves the database
        // consistent if the process is killed — the history must survive that.
        Execute("PRAGMA journal_mode = WAL;");

        // Off by default in SQLite, and without it the cascade that removes an
        // entry's tags when the entry goes would silently not happen.
        Execute("PRAGMA foreign_keys = ON;");
        Execute("""
            CREATE TABLE IF NOT EXISTS entries (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                text        TEXT    NOT NULL,
                source_app  TEXT    NULL,
                created_at  INTEGER NOT NULL
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_entries_created_at ON entries (created_at DESC);");

        Migrate();
    }

    /// <summary>
    /// Brings an older database up to date in place.
    ///
    /// The history is the point of this application, so migrations only ever
    /// add: a user who upgrades must find everything they had, not an empty
    /// list and no explanation.
    /// </summary>
    private void Migrate()
    {
        var from = ReadSchemaVersion();

        if (from < 2)
        {
            // Text entries predate images, so they default to kind 0 and carry
            // no thumbnail or original — exactly what an existing row means.
            Execute("ALTER TABLE entries ADD COLUMN kind INTEGER NOT NULL DEFAULT 0;");
            Execute("ALTER TABLE entries ADD COLUMN thumbnail BLOB NULL;");
            Execute("ALTER TABLE entries ADD COLUMN original_path TEXT NULL;");
        }

        if (from < 3)
        {
            Execute("ALTER TABLE entries ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0;");

            // Tags are their own rows rather than a comma-separated column, so
            // renaming one is a single update and filtering by one is an index
            // lookup instead of a scan with string matching.
            Execute("""
                CREATE TABLE IF NOT EXISTS tags (
                    id   INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE
                );
                """);
            Execute("""
                CREATE TABLE IF NOT EXISTS entry_tags (
                    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                    tag_id   INTEGER NOT NULL REFERENCES tags(id)    ON DELETE CASCADE,
                    PRIMARY KEY (entry_id, tag_id)
                );
                """);
            Execute("CREATE INDEX IF NOT EXISTS idx_entry_tags_tag ON entry_tags (tag_id);");
        }

        if (from < 4)
        {
            // One row per source application: its icon, or a tombstone saying
            // none was found. A thousand entries share one cached copy, and an
            // uninstalled application keeps showing what was extracted while
            // it was still alive.
            Execute("""
                CREATE TABLE IF NOT EXISTS applications (
                    name TEXT PRIMARY KEY,
                    icon BLOB NULL
                );
                """);
        }

        if (from < 5)
        {
            // What a text entry is — link, email, colour, path — recorded at
            // write time so lists and filters never re-derive it per row.
            Execute("ALTER TABLE entries ADD COLUMN sub_type TEXT NULL;");
        }

        if (from < 6)
        {
            // The formatted forms of a rich copy, kept for pasting back with
            // formatting. Existing rows have none, and stay as they are.
            Execute("ALTER TABLE entries ADD COLUMN html TEXT NULL;");
            Execute("ALTER TABLE entries ADD COLUMN rtf TEXT NULL;");
        }

        if (from < 7)
        {
            // A file copy's paths, newline-joined and capped. Existing rows
            // have none, and stay as they are.
            Execute("ALTER TABLE entries ADD COLUMN files TEXT NULL;");
        }

        if (from < 8)
        {
            // Organisation, not content: a favourite belongs to a collection
            // without moving; a note is the entry's public face; a use count
            // remembers how often the entry came back.
            Execute("ALTER TABLE entries ADD COLUMN favorite INTEGER NOT NULL DEFAULT 0;");
            Execute("ALTER TABLE entries ADD COLUMN note TEXT NULL;");
            Execute("ALTER TABLE entries ADD COLUMN use_count INTEGER NOT NULL DEFAULT 0;");
        }

        if (from < 9)
        {
            // Groups are vertical containment — "this entry belongs to that
            // pile" — as against tags, which are horizontal labels for
            // filtering. Deleting a group orphans nothing: the foreign key
            // nulls the column and the entry simply returns to ungrouped.
            Execute("""
                CREATE TABLE IF NOT EXISTS groups (
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    name     TEXT    NOT NULL,
                    icon     TEXT    NULL,
                    position INTEGER NOT NULL DEFAULT 0,
                    hidden   INTEGER NOT NULL DEFAULT 0
                );
                """);
            Execute("ALTER TABLE entries ADD COLUMN group_id INTEGER NULL REFERENCES groups(id) ON DELETE SET NULL;");
            Execute("CREATE INDEX IF NOT EXISTS idx_entries_group ON entries (group_id);");
        }

        if (from < 10)
        {
            // A translation the user chose to keep, linked to the entry it
            // came from. The link is a reference, not a leash: delete the
            // original and the translation stays, merely unlinked.
            Execute("ALTER TABLE entries ADD COLUMN translated_from INTEGER NULL REFERENCES entries(id) ON DELETE SET NULL;");
        }

        if (from < 11)
        {
            // The original image's pixel size, so the preview panel can pick
            // its final size from the database without loading a thing. Rows
            // that predate the column get theirs read back out of the
            // thumbnail header — same aspect, and it is already in the row.
            Execute("ALTER TABLE entries ADD COLUMN image_width INTEGER NOT NULL DEFAULT 0;");
            Execute("ALTER TABLE entries ADD COLUMN image_height INTEGER NOT NULL DEFAULT 0;");
            BackfillImageSizes();
        }

        if (from < 12)
        {
            // Every list path — Recent, Page, Find, Search — orders by this
            // exact triple. Without the index each of those is a full scan
            // plus a sort; with it, a page is an index walk that stops at the
            // limit (O-22). It lives here rather than in CreateSchema because
            // the last column of the triple only exists from version 9 on.
            Execute("""
                CREATE INDEX IF NOT EXISTS idx_entries_order
                ON entries (pinned DESC, created_at DESC, id DESC);
                """);

            // One-time version of the subtype backfill that used to run on
            // every open. Rows written since subtypes existed store a value at
            // write time — 'None' when there is nothing more specific — so the
            // only rows with a NULL are ones an older build left behind. This
            // pass classifies those and stamps the rest, after which NULL never
            // recurs and the every-startup rescan is gone (O-22).
            BackfillSubtypesOnce();
        }

        if (from < 13)
        {
            // The wide payloads — thumbnails, HTML, RTF — leave the entries
            // table for a side table keyed by the same id. In the old layout
            // they sat before pinned and note in every record, so listing a
            // page walked each row's overflow pages even though the list
            // never wanted the payloads (O-22).
            SplitBlobsIntoSideTable();
        }

        if (from < 14)
        {
            // The trigram full-text index that finally makes Chinese search
            // indexable (O-22): the default tokeniser refuses to segment
            // Chinese, which is why search was a LIKE scan for so long. A
            // trigram index keeps substring semantics — every three-character
            // window of the text is a token — so 剪贴板 finds 剪贴板历史
            // exactly as LIKE did, without walking the table.
            CreateFullTextIndex();
        }

        if (from < 15)
        {
            // 翻译记录（用户需求 2026-10-09）：翻译框与反向输入框的每次翻译一行，与剪贴板
            // 条目分开——它们不进窄条，按自己的周期清空，删掉也不碰任何条目。
            Execute("""
                CREATE TABLE IF NOT EXISTS translation_log (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    original    TEXT    NOT NULL,
                    translated  TEXT    NOT NULL,
                    origin      TEXT    NOT NULL,
                    template    TEXT    NOT NULL,
                    created_at  INTEGER NOT NULL
                );
                """);
            Execute("CREATE INDEX IF NOT EXISTS idx_translation_log_created_at ON translation_log (created_at DESC);");
        }

        if (from != SchemaVersion)
        {
            Execute($"PRAGMA user_version = {SchemaVersion};");
        }
    }

    /// <summary>
    /// Fills <c>image_width</c>/<c>image_height</c> for image rows that have
    /// neither, from the thumbnail's IHDR chunk. Idempotent by the WHERE
    /// clause, so a database that arrives half-backfilled finishes the job on
    /// the next open.
    /// </summary>
    private void BackfillImageSizes()
    {
        using var read = _connection.CreateCommand();
        read.CommandText = "SELECT id, thumbnail FROM entries WHERE kind = 1 AND image_width = 0;";
        var pending = new List<(long Id, int Width, int Height)>();

        using (read)
        {
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetValue(1) is byte[] { Length: > 0 } png
                    && PngSize.Read(png) is { } size)
                {
                    pending.Add((reader.GetInt64(0), size.Width, size.Height));
                }
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        using var write = _connection.CreateCommand();
        write.CommandText = """
            UPDATE entries SET image_width = $w, image_height = $h WHERE id = $id;
            """;
        var id = write.CreateParameter();
        id.ParameterName = "$id";
        var width = write.CreateParameter();
        width.ParameterName = "$w";
        var height = write.CreateParameter();
        height.ParameterName = "$h";
        write.Parameters.AddRange([id, width, height]);

        foreach (var (rowId, w, h) in pending)
        {
            id.Value = rowId;
            width.Value = w;
            height.Value = h;
            write.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Classifies any rows recorded before subtypes existed, and stamps the
    /// rest with the 'None' sentinel — see <see cref="SubtypeSentinel"/>.
    ///
    /// Runs once, from the migration that owns it, not on every open: the
    /// every-open version re-selected all plain text forever because plain
    /// text was stored as NULL and NULL was exactly what it looked for.
    /// Interrupted mid-run it simply runs again — version 12 is only stamped
    /// after the last row is non-NULL — so a killed upgrade finishes itself on
    /// the next open.
    /// </summary>
    private void BackfillSubtypesOnce()
    {
        var updates = new List<(long id, string subtype)>();

        // The reader is fully closed before any write: SQLite allows one
        // statement at a time per connection, and a cursor held open across
        // the update turns a routine backfill into a lock error.
        using (var read = _connection.CreateCommand())
        {
            read.CommandText = "SELECT id, text FROM entries WHERE sub_type IS NULL AND kind = 0;";

            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                updates.Add((reader.GetInt64(0), SubtypeClassifier.Detect(reader.GetString(1)).ToString()));
            }
        }

        if (updates.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();

        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE entries SET sub_type = $subtype WHERE id = $id;";
        var subtypeParameter = update.CreateParameter();
        subtypeParameter.ParameterName = "$subtype";
        update.Parameters.Add(subtypeParameter);
        var idParameter = update.CreateParameter();
        idParameter.ParameterName = "$id";
        update.Parameters.Add(idParameter);

        foreach (var (id, subtype) in updates)
        {
            subtypeParameter.Value = subtype;
            idParameter.Value = id;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Copies thumbnail/html/rtf from the entries table into
    /// <c>entry_blobs</c>, then drops the old columns.
    ///
    /// The copy runs in batches, each its own transaction, so an upgrade of a
    /// large history never holds one unbounded transaction open. Both
    /// statements of a batch are keyed on "has a payload AND already has a
    /// side-table row", which makes the whole pass idempotent: interrupted at
    /// any point, the next open simply continues with the rows that still
    /// carry payloads — version 13 is stamped only after the last one moved.
    ///
    /// The copy is pure SQL (INSERT…SELECT) rather than read-into-memory:
    /// a batch of five hundred rich entries can be half a gigabyte, and the
    /// per-batch payload never needs to exist in the process at all.
    /// </summary>
    private void SplitBlobsIntoSideTable()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS entry_blobs (
                entry_id  INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
                thumbnail BLOB NULL,
                html      TEXT NULL,
                rtf       TEXT NULL
            );
            """);

        // A database arriving here through the version ladder always has the
        // three columns — version 2 added them long before this step runs. The
        // guard is for the one shape that can arrive without them: a database
        // whose version was set back by hand (tests simulate old versions that
        // way), where there is simply nothing to move.
        if (!HasColumn("thumbnail"))
        {
            return;
        }

        const int batch = 500;

        while (true)
        {
            using var write = _connection.BeginTransaction();

            long copied;
            using (var copy = _connection.CreateCommand())
            {
                copy.Transaction = write;
                copy.CommandText = $"""
                    INSERT INTO entry_blobs (entry_id, thumbnail, html, rtf)
                    SELECT id, thumbnail, html, rtf FROM entries
                    WHERE (thumbnail IS NOT NULL OR html IS NOT NULL OR rtf IS NOT NULL)
                      AND NOT EXISTS (SELECT 1 FROM entry_blobs b WHERE b.entry_id = entries.id)
                    LIMIT {batch}
                    RETURNING entry_id;
                    """;
                using var reader = copy.ExecuteReader();
                copied = 0;
                while (reader.Read())
                {
                    copied++;
                }
            }

            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = write;
                clear.CommandText = """
                    UPDATE entries SET thumbnail = NULL, html = NULL, rtf = NULL
                    WHERE (thumbnail IS NOT NULL OR html IS NOT NULL OR rtf IS NOT NULL)
                      AND EXISTS (SELECT 1 FROM entry_blobs b WHERE b.entry_id = entries.id);
                    """;
                clear.ExecuteNonQuery();
            }

            write.Commit();

            if (copied < batch)
            {
                break;
            }
        }

        // Drop rather than abandon: a column left in the schema would keep
        // collecting writes. DROP COLUMN hides the column but leaves its bytes
        // in the file, so old rows would still pay the overflow-page walk —
        // VACUUM rewrites every row through the new schema, which is what
        // turns "narrow rows" from an aspiration into a property of the file.
        // It runs once, here, outside any transaction, as VACUUM requires.
        Execute("ALTER TABLE entries DROP COLUMN thumbnail;");
        Execute("ALTER TABLE entries DROP COLUMN html;");
        Execute("ALTER TABLE entries DROP COLUMN rtf;");
        Execute("VACUUM;");
    }

    /// <summary>
    /// The trigram full-text index over the entries' text, and the triggers
    /// that keep it in step with every write path — including the ones inside
    /// an import transaction, which is exactly why sync lives in triggers
    /// rather than at call sites: a rollback rolls the index back with the
    /// rows, and no future write path can forget it.
    ///
    /// External content (<c>content='entries'</c>): the text is stored once,
    /// in the table that owns it; the index holds only the trigrams. The
    /// 'delete' commands in the triggers hand FTS the old text because with
    /// external content it cannot re-read what is already gone.
    ///
    /// The rebuild command repopulates from the content table in one atomic
    /// statement, so a database that arrives mid-upgrade either has the whole
    /// index or none of it, and the migration simply runs again.
    /// </summary>
    private void CreateFullTextIndex()
    {
        Execute("""
            CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                text,
                tokenize = 'trigram',
                content = 'entries',
                content_rowid = 'id');
            """);

        Execute("""
            CREATE TRIGGER IF NOT EXISTS entries_fts_insert AFTER INSERT ON entries BEGIN
                INSERT INTO entries_fts (rowid, text) VALUES (new.id, new.text);
            END;
            """);
        Execute("""
            CREATE TRIGGER IF NOT EXISTS entries_fts_delete AFTER DELETE ON entries BEGIN
                INSERT INTO entries_fts (entries_fts, rowid, text) VALUES ('delete', old.id, old.text);
            END;
            """);
        Execute("""
            CREATE TRIGGER IF NOT EXISTS entries_fts_update AFTER UPDATE OF text ON entries BEGIN
                INSERT INTO entries_fts (entries_fts, rowid, text) VALUES ('delete', old.id, old.text);
                INSERT INTO entries_fts (rowid, text) VALUES (new.id, new.text);
            END;
            """);

        Execute("INSERT INTO entries_fts (entries_fts) VALUES ('rebuild');");
    }

    private int ReadSchemaVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());

        // A database created before versioning began still has the original
        // three columns and reports zero; treat it as version 1.
        return version == 0 && HasColumn("kind") ? 2 : Math.Max(version, 1);
    }

    private bool HasColumn(string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('entries') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    public Entry Append(
        string text,
        string? sourceApp,
        DateTimeOffset createdAt,
        string? html = null,
        string? rtf = null)
        => Write(() =>
        {
            lock (_gate)
        {
            var subtype = SubtypeClassifier.Detect(text);

            // One transaction: the row and its payloads (when there are any)
            // land together or not at all.
            using var write = BeginWrite();

            long id;
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = write.Transaction;
                command.CommandText = """
                    INSERT INTO entries (text, source_app, created_at, sub_type)
                    VALUES ($text, $sourceApp, $createdAt, $subtype)
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("$text", text);
                command.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
                command.Parameters.AddWithValue("$createdAt", createdAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$subtype", subtype.ToString());

                id = (long)command.ExecuteScalar()!;
            }

            InsertBlobsIn(write.Transaction, id, thumbnail: null, html, rtf);

            write.Commit();
            CountChanged();
            return new Entry(id, text, sourceApp, createdAt)
            {
                Subtype = subtype,
                Html = html,
                Rtf = rtf,
            };
        }
        });

    /// <summary>
    /// Records a file copy: the label is what the list shows, the paths are
    /// what a paste back needs, already capped by the caller.
    /// </summary>
    public Entry AppendFiles(
        IReadOnlyList<string> paths,
        string? sourceApp,
        DateTimeOffset createdAt)
        => Write(() =>
        {
            lock (_gate)
        {
            var capped = paths.Count > FileEntries.Cap;
            var kept = FileEntries.WithinCap(paths, out _);
            var label = FileEntries.Label(paths, capped);

            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO entries (text, source_app, created_at, kind, files)
                VALUES ($label, $sourceApp, $createdAt, 2, $files)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$label", label);
            command.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", createdAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$files", string.Join("\n", kept));

            var id = (long)command.ExecuteScalar()!;
            CountChanged();
            return new Entry(id, label, sourceApp, createdAt)
            {
                Kind = EntryKind.Files,
                Files = kept,
            };
        }
        });

    /// <summary>
    /// Whether the application has a row in the icon store — including a
    /// tombstone row for an icon that could not be found.
    /// </summary>
    public bool HasApplicationIcon(string name)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM applications WHERE name = $name;";
            command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
    }

    /// <summary>The cached icon's PNG bytes, or null when none was found.</summary>
    public byte[]? ApplicationIcon(string name)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT icon FROM applications WHERE name = $name;";
            command.Parameters.AddWithValue("$name", name);
            var result = command.ExecuteScalar();
            return result is byte[] png ? png : null;
        }
    }

    /// <summary>
    /// Stores the icon row, ignoring the call when one already exists — see
    /// <see cref="SourceIconCache"/> for why a race must not overwrite.
    /// </summary>
    public void SaveApplicationIcon(string name, byte[]? png)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO applications (name, icon)
                VALUES ($name, $icon);
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$icon", (object?)png ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records a copied image: the thumbnail goes in the database and stays
    /// there, the full-size original goes on disk and is subject to retention.
    /// </summary>
    public Entry AppendImage(
        string label,
        byte[] thumbnailPng,
        string originalPath,
        string? sourceApp,
        DateTimeOffset createdAt,
        int width = 0,
        int height = 0)
        => Write(() =>
        {
            lock (_gate)
        {
            using var write = BeginWrite();

            long id;
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = write.Transaction;
                command.CommandText = """
                    INSERT INTO entries (text, source_app, created_at, kind, original_path, image_width, image_height)
                    VALUES ($text, $sourceApp, $createdAt, $kind, $originalPath, $w, $h)
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("$text", label);
                command.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
                command.Parameters.AddWithValue("$createdAt", createdAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$kind", (int)EntryKind.Image);
                command.Parameters.AddWithValue("$originalPath", originalPath);
                command.Parameters.AddWithValue("$w", width);
                command.Parameters.AddWithValue("$h", height);

                id = (long)command.ExecuteScalar()!;
            }

            InsertBlobsIn(write.Transaction, id, thumbnailPng, html: null, rtf: null);

            write.Commit();
            CountChanged();
            return new Entry(id, label, sourceApp, createdAt)
            {
                Kind = EntryKind.Image,
                ThumbnailPng = thumbnailPng,
                OriginalPath = originalPath,
                ImageWidth = width,
                ImageHeight = height,
            };
        }
        });

    /// <summary>
    /// Writes an entry's payload row, when there is one. Callers pass the
    /// transaction they already hold so the row and its payloads commit
    /// together; a NULL-only row is skipped — the side table has no row for
    /// the entries that never had anything to store.
    /// </summary>
    private void InsertBlobsIn(SqliteTransaction transaction, long id, byte[]? thumbnail, string? html, string? rtf)
    {
        if (thumbnail is null && html is null && rtf is null)
        {
            return;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entry_blobs (entry_id, thumbnail, html, rtf)
            VALUES ($id, $thumbnail, $html, $rtf);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$thumbnail", (object?)thumbnail ?? DBNull.Value);
        command.Parameters.AddWithValue("$html", (object?)html ?? DBNull.Value);
        command.Parameters.AddWithValue("$rtf", (object?)rtf ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Forgets where an original used to be, once retention has deleted it.
    /// The entry and its thumbnail are untouched.
    /// </summary>
    public void ClearOriginal(long id)
        => Write(() =>
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "UPDATE entries SET original_path = NULL WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
        });

    /// <summary>Every image entry that still has an original on disk.</summary>
    public IReadOnlyList<Entry> ImagesWithOriginals(bool keepFavorites = false, bool keepPinned = false)
    {
        lock (_gate)
        {
            return ImagesWhere($"original_path IS NOT NULL AND NOT ({ProtectedConditionFor(keepFavorites, keepPinned)})");
        }
    }

    /// <summary>Image entries with originals, created within the range.</summary>
    public IReadOnlyList<Entry> ImagesCreatedBetween(
        DateTimeOffset from, DateTimeOffset to, bool keepFavorites = false, bool keepPinned = false)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NarrowColumns},
                       {TagsColumn}
                FROM entries
                WHERE kind = $kind AND original_path IS NOT NULL
                  AND created_at BETWEEN $from AND $to
                  AND NOT ({ProtectedConditionFor(keepFavorites, keepPinned)});
                """;
            command.Parameters.AddWithValue("$kind", (int)EntryKind.Image);
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

            return ReadEntries(command);
        }
    }

    private IReadOnlyList<Entry> ImagesWhere(string condition)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT {NarrowColumns},
                   {TagsColumn}
            FROM entries
            WHERE kind = $kind AND {condition};
            """;
        command.Parameters.AddWithValue("$kind", (int)EntryKind.Image);

        return ReadEntries(command);
    }

    /// <summary>Image entries whose original is older than the cutoff.</summary>
    public IReadOnlyList<Entry> ImagesWithOriginalsBefore(
        DateTimeOffset cutoff, int limit, bool keepFavorites = false, bool keepPinned = false)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NarrowColumns},
                       {TagsColumn}
                FROM entries
                WHERE kind = $kind AND original_path IS NOT NULL AND created_at < $cutoff
                  AND NOT ({ProtectedConditionFor(keepFavorites, keepPinned)})
                ORDER BY created_at ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$kind", (int)EntryKind.Image);
            command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$limit", limit);

            return ReadEntries(command);
        }
    }

    /// <summary>
    /// Writes many entries in one transaction. Appending them one by one costs
    /// a commit each, which turns a bulk write into a wait measured in minutes.
    /// </summary>
    public void AppendMany(IEnumerable<NewEntry> entries)
        => Write(() =>
        {
            lock (_gate)
        {
            using var write = BeginWrite();
            using var command = _connection.CreateCommand();
            command.Transaction = write.Transaction;
            command.CommandText = """
                INSERT INTO entries (text, source_app, created_at, sub_type)
                VALUES ($text, $sourceApp, $createdAt, $subtype);
                """;

            var text = command.Parameters.Add("$text", SqliteType.Text);
            var sourceApp = command.Parameters.Add("$sourceApp", SqliteType.Text);
            var createdAt = command.Parameters.Add("$createdAt", SqliteType.Integer);
            var subtype = command.Parameters.Add("$subtype", SqliteType.Text);

            foreach (var entry in entries)
            {
                text.Value = entry.Text;
                sourceApp.Value = (object?)entry.SourceApp ?? DBNull.Value;
                createdAt.Value = entry.CreatedAt.ToUnixTimeMilliseconds();

                // Classified at write time like every other path — the startup
                // backfill that used to sweep NULLs afterwards is gone (O-22).
                subtype.Value = SubtypeClassifier.Detect(entry.Text).ToString();
                command.ExecuteNonQuery();
            }

            write.Commit();
            CountChanged();
        }
        });

    /// <summary>
    /// The last thing copied, or null when the history is empty. Ordered by
    /// time alone: the pipeline's duplicate check asks "was this the previous
    /// copy", and a pinned entry from last week never is.
    /// </summary>
    public Entry? MostRecent()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NarrowColumns},
                       {TagsColumn}
                FROM entries
                ORDER BY created_at DESC, id DESC
                LIMIT 1;
                """;

            return ReadEntries(command).FirstOrDefault();
        }
    }

    /// <summary>
    /// Finds entries whose text contains <paramref name="query"/>, newest first.
    ///
    /// The trigram index answers three characters and up — Chinese included,
    /// which the default tokeniser cannot segment — with exact substring
    /// semantics. Shorter queries fall back to a LIKE scan of the narrow
    /// table: no trigram exists below three characters, so the index has
    /// nothing to say, and one or two characters over a narrow row is a cheap
    /// question.
    /// </summary>
    public IReadOnlyList<Entry> Search(string query, int limit, int offset = 0)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return [];
            }

            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NarrowColumns},
                       {TagsColumn}
                FROM entries
                WHERE {TextContains(query, command)}
                ORDER BY pinned DESC, created_at DESC, id DESC
                LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            return ReadEntries(command);
        }
    }

    /// <summary>
    /// Finds entries matching every part of the filter, newest first.
    ///
    /// One query rather than filtering a search in memory: combining a keyword
    /// with a date range has to narrow the whole history, not just whatever
    /// the keyword happened to return first. Paged by cursor, like
    /// <see cref="Page(int, PageCursor?)"/>.
    /// </summary>
    public IReadOnlyList<Entry> Find(HistoryFilter filter, int limit, PageCursor? after = null)
    {
        lock (_gate)
        {
            var conditions = new List<string>();

            using var command = _connection.CreateCommand();

            BuildFilterConditions(filter, command, conditions);
            AddCursorCondition(command, conditions, after);

            var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);

            command.CommandText = $"""
                SELECT {NarrowColumns},
                       {TagsColumn}
                FROM entries
                {where}
                ORDER BY pinned DESC, created_at DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            return ReadEntries(command);
        }
    }

    /// <summary>
    /// How many entries the filter matches in total — the number a filtered
    /// list's footer owes the user ("3 / 161 条"), which a paged list cannot
    /// know from memory: it only holds the loaded page.
    /// </summary>
    public int CountMatching(HistoryFilter filter)
    {
        lock (_gate)
        {
            var conditions = new List<string>();

            using var command = _connection.CreateCommand();

            BuildFilterConditions(filter, command, conditions);

            var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);

            command.CommandText = $"SELECT COUNT(*) FROM entries {where};";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    /// <summary>
    /// The WHERE clauses a filter compiles to, with their parameters bound onto
    /// <paramref name="command"/>. Shared by the paged reader and the counter
    /// so the two can never disagree about what a filter means.
    /// </summary>
    private static void BuildFilterConditions(HistoryFilter filter, SqliteCommand command, List<string> conditions)
    {
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            // The note is searchable alongside the text: "the brand blue one"
            // has to find the entry whose content is a bare hex code. The
            // note is short and unindexed, so it stays a LIKE either way —
            // only the text has a trigram index worth consulting.
            conditions.Add($"({TextContains(filter.Query, command)} OR note LIKE $notePattern ESCAPE '\\')");
            command.Parameters.AddWithValue("$notePattern", $"%{EscapeForLike(filter.Query)}%");
        }

        if (filter.Favorite is { } favoriteOnly && favoriteOnly)
        {
            conditions.Add("favorite = 1");
        }

        if (filter.From is { } from)
        {
            conditions.Add("created_at >= $from");
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        }

        if (filter.To is { } to)
        {
            conditions.Add("created_at <= $to");
            command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        }

        if (filter.Kind is { } kind)
        {
            conditions.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", (int)kind);
        }

        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            conditions.Add("""
                id IN (SELECT et.entry_id FROM entry_tags et
                         JOIN tags t ON t.id = et.tag_id
                        WHERE t.name = $tag)
                """);
            command.Parameters.AddWithValue("$tag", filter.Tag.Trim());
        }

        if (filter.Subtype is { } subtype)
        {
            // One filter choice, two stored values: a "path" filter means
            // both local and UNC, because to the user they are one idea.
            if (subtype == EntrySubtype.LocalPath)
            {
                conditions.Add("sub_type IN ('LocalPath', 'UncPath')");
            }
            else
            {
                conditions.Add("sub_type = $subtype");
                command.Parameters.AddWithValue("$subtype", subtype.ToString());
            }
        }

        if (filter.Group is { } group)
        {
            conditions.Add("group_id = $group");
            command.Parameters.AddWithValue("$group", group);
        }
    }

    /// <summary>
    /// A window onto the history, newest first. Every read path takes a limit:
    /// the history is never loaded into memory in one piece, however large it
    /// grows.
    /// </summary>
    /// <summary>
    /// A window onto the history, newest first. Every read path takes a limit:
    /// the history is never loaded into memory in one piece, however large it
    /// grows. The window is positioned by cursor — the sort key of the last
    /// row read — so a history that grows while the user scrolls neither
    /// repeats nor skips rows the way deep OFFSETs do.
    /// </summary>
    public IReadOnlyList<Entry> Page(int limit, PageCursor? after = null)
    {
        lock (_gate)
        {
            var conditions = new List<string>();

            using var command = _connection.CreateCommand();
            AddCursorCondition(command, conditions, after);

            var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);

            command.CommandText = $"""
                SELECT {NarrowColumns},
                       {TagsColumn}
                FROM entries
                {where}
                ORDER BY pinned DESC, created_at DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            return ReadEntries(command);
        }
    }

    /// <summary>
    /// The condition that continues a paged read after <paramref name="after"/>:
    /// row values compared against the index's own ordering, so the page is an
    /// index walk down from the cursor rather than a walk over everything
    /// above it — which is what OFFSET does, every page, again. Nothing is
    /// added for the first page.
    /// </summary>
    private static void AddCursorCondition(SqliteCommand command, List<string> conditions, PageCursor? after)
    {
        if (after is not { } cursor)
        {
            return;
        }

        command.Parameters.AddWithValue("$cursorPinned", cursor.Pinned);
        command.Parameters.AddWithValue("$cursorCreatedAt", cursor.CreatedAtMs);
        command.Parameters.AddWithValue("$cursorId", cursor.Id);
        conditions.Add("(pinned, created_at, id) < ($cursorPinned, $cursorCreatedAt, $cursorId)");
    }

    public int Count()
    {
        lock (_gate)
        {
            // The narrow bar and library footer ask on every refresh; the
            // answer changes only when a write lands, and every write drops
            // the cache inside this same gate.
            if (_countCache is { } cached)
            {
                return cached;
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM entries;";
            _countCache = Convert.ToInt32(command.ExecuteScalar());
            return _countCache.Value;
        }
    }

    /// <summary>
    /// Called inside the gate by every write that can add or remove rows, so
    /// <see cref="Count"/>'s cache can never answer with a number the table
    /// has moved on from.
    /// </summary>
    private void CountChanged() => _countCache = null;

    /// <summary>
    /// The payloads for a page of entries, keyed by id. Lists deliberately
    /// read the narrow table; the window showing a page of thumbnails asks
    /// this once per page — a handful of primary-key lookups — instead of
    /// carrying every thumbnail through every list query.
    /// </summary>
    public IReadOnlyDictionary<long, EntryBlobs> BlobsOf(IReadOnlyCollection<long> ids)
    {
        lock (_gate)
        {
            if (ids.Count == 0)
            {
                return new Dictionary<long, EntryBlobs>();
            }

            var names = string.Join(", ", Enumerable.Range(0, ids.Count).Select(index => $"$id{index}"));
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT entry_id, thumbnail, html, rtf
                FROM entry_blobs
                WHERE entry_id IN ({names});
                """;

            var index = 0;
            foreach (var id in ids)
            {
                command.Parameters.AddWithValue($"$id{index++}", id);
            }

            var blobs = new Dictionary<long, EntryBlobs>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                blobs[reader.GetInt64(0)] = new EntryBlobs(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : (byte[])reader[1],
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }

            return blobs;
        }
    }

    /// <summary>
    /// Entries with their payloads attached, oldest id first, walking by id
    /// cursor — the export and merge-scan read. OFFSET paging would re-walk
    /// the whole table once per page at exactly the sizes that need exporting;
    /// a cursor reads each row once.
    /// </summary>
    public IReadOnlyList<Entry> EntriesWithBlobsAfter(long afterId, int limit)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NarrowColumns},
                       b.thumbnail, b.html, b.rtf,
                       {TagsColumn}
                FROM entries
                LEFT JOIN entry_blobs b ON b.entry_id = entries.id
                WHERE entries.id > $after
                ORDER BY entries.id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$after", afterId);
            command.Parameters.AddWithValue("$limit", limit);

            return ReadEntriesWithBlobs(command);
        }
    }

    /// <summary>
    /// How many entries the current protection settings would spare — the
    /// number a confirmation owes the user before a bulk delete.
    /// </summary>
    public int CountProtected(bool keepFavorites, bool keepPinned)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM entries WHERE {ProtectedConditionFor(keepFavorites, keepPinned)};";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    /// <summary>The protected entries inside a time range — for a range delete's confirmation copy.</summary>
    public int CountProtectedBetween(
        DateTimeOffset from, DateTimeOffset to, bool keepFavorites, bool keepPinned)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(*) FROM entries
                WHERE created_at BETWEEN $from AND $to
                  AND {ProtectedConditionFor(keepFavorites, keepPinned)};
                """;
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    /// <summary>Returns whether there was anything to delete.</summary>
    public bool Delete(long id)
        => Write(() =>
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM entries WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                var removed = command.ExecuteNonQuery() > 0;
                if (removed)
                {
                    CountChanged();
                }

                return removed;
            }
        });

    /// <summary>
    /// Deletes entries created within the range, both ends included.
    ///
    /// Protected entries — favourites and pins, when their switches are on —
    /// are spared: bulk deletes are the delete a user does not look at each
    /// row of, which is exactly where a marker worth keeping should count.
    /// </summary>
    public int DeleteCreatedBetween(
        DateTimeOffset from, DateTimeOffset to, bool keepFavorites = false, bool keepPinned = false)
        => Write(() =>
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"""
                    DELETE FROM entries
                    WHERE created_at BETWEEN $from AND $to
                      AND NOT ({ProtectedConditionFor(keepFavorites, keepPinned)});
                    """;
                command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
                var removed = command.ExecuteNonQuery();
                if (removed > 0)
                {
                    CountChanged();
                }

                return removed;
            }
        });

    /// <summary>
    /// The SQL that says an entry is protected — literally, so any caller can
    /// embed it without binding parameters by hand. The whole OR chain is
    /// wrapped: unwrapped, SQL precedence would read
    /// "range AND favourite" OR "pinned anywhere", which is not the promise.
    /// </summary>
    private static string ProtectedConditionFor(bool keepFavorites, bool keepPinned)
    {
        var clauses = new List<string>();
        if (keepFavorites)
        {
            clauses.Add("(favorite = 1)");
        }

        if (keepPinned)
        {
            clauses.Add("(pinned = 1)");
        }

        return clauses.Count == 0 ? "0" : $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>
    /// Clears the history. Protected entries stay when their switches are on —
    /// "clear everything" is exactly the moment a user would rather keep the
    /// pile they so carefully starred — and the confirmation copy says so.
    /// </summary>
    public int DeleteAll(bool keepFavorites = false, bool keepPinned = false)
        => Write(() =>
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"DELETE FROM entries WHERE NOT ({ProtectedConditionFor(keepFavorites, keepPinned)});";
                var removed = command.ExecuteNonQuery();
                if (removed > 0)
                {
                    CountChanged();
                }

                return removed;
            }
        });

    /// <summary>
    /// Neutralises the wildcards LIKE would otherwise read in a user's query —
    /// an unescaped "%" would silently match the entire history.
    /// </summary>
    private static string EscapeForLike(string query)
        => query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// The SQL that says "the text contains this query", with the query's
    /// parameter bound onto <paramref name="command"/>. Three characters and
    /// up is a trigram MATCH — the phrase is double-quoted and inner quotes
    /// doubled, so every character of a user's query (operators included) is
    /// taken literally rather than parsed as FTS syntax. Below three
    /// characters there is no trigram to match, and the narrow table is
    /// scanned the old way.
    /// </summary>
    private static string TextContains(string query, SqliteCommand command)
    {
        if (query.Length >= 3)
        {
            command.Parameters.AddWithValue("$ftsQuery", "\"" + query.Replace("\"", "\"\"") + "\"");
            return "id IN (SELECT rowid FROM entries_fts WHERE entries_fts MATCH $ftsQuery)";
        }

        command.Parameters.AddWithValue("$textPattern", $"%{EscapeForLike(query)}%");
        return "text LIKE $textPattern ESCAPE '\\'";
    }

    /// <summary>
    /// Moves an existing entry to the top of the history without creating a
    /// second copy of it.
    /// </summary>
    public void Touch(long id, DateTimeOffset at)
        => Write(() =>
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "UPDATE entries SET created_at = $createdAt WHERE id = $id;";
                command.Parameters.AddWithValue("$createdAt", at.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
        });

    public IReadOnlyList<Entry> Recent(int limit)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NarrowColumns},
                       {TagsColumn}
                FROM entries
                ORDER BY pinned DESC, created_at DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            return ReadEntries(command);
        }
    }

    /// <summary>A column written before an unknown value appeared — never worth a broken list over.</summary>
    private static EntrySubtype ParseSubtype(string? stored)
        => stored is not null && Enum.TryParse<EntrySubtype>(stored, out var parsed)
            ? parsed
            : EntrySubtype.None;

    /// <summary>
    /// Maps a narrow row — the columns <see cref="NarrowColumns"/> names, tags
    /// last. Payload columns are absent by design: an entry returned from a
    /// list query carries no thumbnail and no formatted forms, and the caller
    /// that needs them asks <see cref="BlobsOf"/> or <see cref="Get"/>.
    /// </summary>
    private static IReadOnlyList<Entry> ReadEntries(SqliteCommand command)
    {
        var entries = new List<Entry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadNarrowRow(reader, reader.FieldCount - 1));
        }

        return entries;
    }

    /// <summary>
    /// Maps a row that joins <c>entry_blobs</c>: the narrow columns, then
    /// thumbnail/html/rtf, then the tags — the column order
    /// <see cref="EntriesWithBlobsAfter"/> and <see cref="Get"/> select.
    /// </summary>
    private static IReadOnlyList<Entry> ReadEntriesWithBlobs(SqliteCommand command)
    {
        var entries = new List<Entry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadNarrowRow(reader, reader.FieldCount - 1) with
            {
                ThumbnailPng = reader.IsDBNull(reader.FieldCount - 4) ? null : (byte[])reader[reader.FieldCount - 4],
                Html = reader.IsDBNull(reader.FieldCount - 3) ? null : reader.GetString(reader.FieldCount - 3),
                Rtf = reader.IsDBNull(reader.FieldCount - 2) ? null : reader.GetString(reader.FieldCount - 2),
            });
        }

        return entries;
    }

    /// <summary>
    /// Reads one row positioned by its tags column: everything before it is
    /// the narrow set, in <see cref="NarrowColumns"/> order. Field offsets are
    /// taken from the tags column backwards, so adding a narrow column never
    /// silently shuffles a reader.
    /// </summary>
    private static Entry ReadNarrowRow(SqliteDataReader reader, int tagsIndex)
    {
        return new Entry(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)))
        {
            Kind = (EntryKind)reader.GetInt32(4),
            OriginalPath = reader.IsDBNull(5) ? null : reader.GetString(5),
            IsPinned = reader.GetInt32(6) != 0,
            Subtype = ParseSubtype(reader.IsDBNull(7) ? null : reader.GetString(7)),
            Files = reader.IsDBNull(8) || reader.GetString(8).Length == 0
                ? []
                : reader.GetString(8).Split('\n'),
            Favorite = reader.GetInt32(9) != 0,
            Note = reader.IsDBNull(10) ? null : reader.GetString(10),
            UseCount = reader.GetInt32(11),
            GroupId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            TranslatedFrom = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            ImageWidth = reader.GetInt32(14),
            ImageHeight = reader.GetInt32(15),

            // Joined in rather than fetched per row: a list of a hundred
            // entries would otherwise be a hundred extra queries.
            Tags = reader.IsDBNull(tagsIndex)
                ? []
                : reader.GetString(tagsIndex).Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries),
        };
    }

    /// <summary>
    /// Runs <paramref name="work"/> as one transaction: every write inside it
    /// lands together, or — when it throws — none does. Members that would
    /// open a transaction of their own join this one instead, since SQLite
    /// cannot nest them. The gate is held throughout and every other caller
    /// waits, so the work should be writes already prepared — never reading
    /// files or waiting on anything.
    ///
    /// The batch reports one <see cref="Changed"/> after it commits, however
    /// many writes are inside it; one that throws reports nothing.
    /// </summary>
    internal void RunInTransaction(Action work)
        => Write(() =>
        {
            lock (_gate)
            {
                if (_batch is not null)
                {
                    work();
                    return;
                }

                using var transaction = _connection.BeginTransaction();
                _batch = transaction;
                try
                {
                    work();
                    transaction.Commit();
                }
                finally
                {
                    _batch = null;
                }
            }
        });

    /// <summary>A write that spans statements: inside the open batch when there is one, else in its own transaction.</summary>
    private WriteScope BeginWrite()
        => _batch is { } open
            ? new WriteScope(open, owned: false)
            : new WriteScope(_connection.BeginTransaction(), owned: true);

    private readonly struct WriteScope(SqliteTransaction transaction, bool owned) : IDisposable
    {
        public SqliteTransaction Transaction => transaction;

        /// <summary>A joined write commits when its batch does, never on its own.</summary>
        public void Commit()
        {
            if (owned)
            {
                transaction.Commit();
            }
        }

        public void Dispose()
        {
            if (owned)
            {
                transaction.Dispose();
            }
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
