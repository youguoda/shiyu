using Microsoft.Data.Sqlite;

namespace DictionaryBuilder;

/// <summary>一次查询的结果。</summary>
/// <param name="Query">用户给的原文。</param>
/// <param name="Word">命中的词头（已是小写键）。</param>
/// <param name="ViaForm">如果是经词形表找到的：查的词形（如 went）；直接命中则为 null。</param>
internal sealed record LookupResult(string Query, string Word, string? ViaForm, string? Phonetic, string Translation, string? Tag);

/// <summary>词典 SQLite 文件的写入和查询。表结构见 CreateSchema。</summary>
internal static class DictionaryDb
{
    /// <summary>表结构版本，写进 PRAGMA user_version。App 可以据此判断认不认识这个文件。</summary>
    public const int SchemaVersion = 1;

    private const string CreateSchema = """
        CREATE TABLE words(word TEXT PRIMARY KEY, phonetic TEXT, translation TEXT NOT NULL, tag TEXT) WITHOUT ROWID;
        CREATE TABLE forms(form TEXT PRIMARY KEY, lemma TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
        """;

    // ---- 写入 ----------------------------------------------------------------

    /// <summary>
    /// 写出整个词典文件（已存在则覆盖）。行按键的字节序插入，所以同样的输入总是得到同样的文件。
    /// phonetic / tag 为空时存 NULL。
    /// </summary>
    public static void Write(
        string path,
        IReadOnlyList<EcdictEntry> words,
        IReadOnlyList<KeyValuePair<string, string>> forms,
        IReadOnlyList<KeyValuePair<string, string>> meta,
        int pageSize)
    {
        if (File.Exists(path)) File.Delete(path);

        using (var db = Open(path, create: true))
        {
            // 批量导入，崩了重来即可，不需要日志和同步。page_size 必须在建表之前设。
            Execute(db, $"PRAGMA page_size = {pageSize}");
            Execute(db, "PRAGMA journal_mode = OFF");
            Execute(db, "PRAGMA synchronous = OFF");
            Execute(db, CreateSchema);

            using (var tx = db.BeginTransaction())
            {
                InsertWords(db, tx, words);
                InsertPairs(db, tx, "INSERT INTO forms(form, lemma) VALUES ($a, $b)", forms);
                InsertPairs(db, tx, "INSERT INTO meta(key, value) VALUES ($a, $b)", meta);
                tx.Commit();
            }

            Execute(db, $"PRAGMA user_version = {SchemaVersion}");
            Execute(db, "VACUUM"); // 重排页面，文件最紧凑
        }

        // 连接池已关（Pooling=False），文件句柄此刻已释放。
    }

    private static void InsertWords(SqliteConnection db, SqliteTransaction tx, IReadOnlyList<EcdictEntry> words)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO words(word, phonetic, translation, tag) VALUES ($w, $p, $t, $g)";
        var w = cmd.Parameters.Add("$w", SqliteType.Text);
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        var t = cmd.Parameters.Add("$t", SqliteType.Text);
        var g = cmd.Parameters.Add("$g", SqliteType.Text);

        foreach (var e in words)
        {
            w.Value = e.Key;
            p.Value = NullIfEmpty(e.Phonetic);
            t.Value = e.Translation;
            g.Value = NullIfEmpty(e.Tag);
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertPairs(SqliteConnection db, SqliteTransaction tx, string sql, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        var a = cmd.Parameters.Add("$a", SqliteType.Text);
        var b = cmd.Parameters.Add("$b", SqliteType.Text);

        foreach (var (key, value) in pairs)
        {
            a.Value = key;
            b.Value = value;
            cmd.ExecuteNonQuery();
        }
    }

    private static object NullIfEmpty(string s) => s.Length == 0 ? DBNull.Value : s;

    // ---- 查询 ----------------------------------------------------------------

    /// <summary>
    /// 查询键的规范化：去首尾空白、小写，并把弯引号撇号（’ ‘）换成直撇号——
    /// 网页和文档里选出来的 don’t 用的是弯撇号，而词典里存的是 don't。
    /// App 查词前要做同样的处理。
    /// </summary>
    public static string NormalizeQuery(string text) =>
        text.Trim().Replace('’', '\'').Replace('‘', '\'').ToLowerInvariant();

    /// <summary>
    /// 先查词头；没有再查词形表换成原形，再查词头。都没有返回 null。
    /// </summary>
    public static LookupResult? Lookup(SqliteConnection db, string text)
    {
        var key = NormalizeQuery(text);

        var direct = GetWord(db, key);
        if (direct is not null) return direct with { Query = text };

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT lemma FROM forms WHERE form = $f";
        cmd.Parameters.AddWithValue("$f", key);
        if (cmd.ExecuteScalar() is not string lemma) return null;

        var viaLemma = GetWord(db, lemma);
        return viaLemma is null ? null : viaLemma with { Query = text, ViaForm = key };
    }

    private static LookupResult? GetWord(SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT phonetic, translation, tag FROM words WHERE word = $w";
        cmd.Parameters.AddWithValue("$w", key);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new LookupResult(
            Query: key,
            Word: key,
            ViaForm: null,
            Phonetic: reader.IsDBNull(0) ? null : reader.GetString(0),
            Translation: reader.GetString(1),
            Tag: reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    // ---- 连接 ----------------------------------------------------------------

    /// <summary>打开词典文件。Pooling=False，这样 Dispose 之后文件立刻可以移动/删除。</summary>
    public static SqliteConnection Open(string path, bool create = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var db = new SqliteConnection(builder.ToString());
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
