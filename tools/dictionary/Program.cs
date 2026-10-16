// 离线词典生成器：把 ECDICT 的 ecdict.csv 裁成一个"常用词"子集，写成 SQLite 文件，
// 供拾语离线显示英文单词的中文释义、音标和考试标签。
//
// 数据来源：https://github.com/skywind3000/ECDICT （MIT 许可，作者 Linwei / skywind3000）。
//
// 用法：
//   dotnet run -c Release -- build  <ecdict.csv> <输出.db> --commit <ECDICT 提交 SHA> [--rank N] [--page-size 4096] [--audit <目录>]
//   dotnet run -c Release -- lookup <词典.db> <单词>...
//
// build 做的事：
//   1. 读 CSV；只留单个英文词（字母，内部可有连字符/撇号）且 translation 里有汉字的条目；
//      文本清理：字面的 "\n" 还原成换行，释义逐行去首尾空白、去空行，音标去掉自带的 [ ] / /；
//   2. 命中任一条件入选：oxford = 1、collins >= 1、tag 非空、frq 或 bnc 名次 <= --rank；
//   3. 折叠成小写后同键的条目只留一个（全小写优先，否则词频最高）；
//      （ECDICT 的词头本来就不分大小写唯一，目前这一步实际不会触发，留着是防它将来变）
//   4. "释义只是 X 的变形"的条目（went = go 的过去式）降级，不当词头，改走词形表；
//      有自己释义的（saw、gone）和 X 里没有的义项（glasses 的"眼镜"）一律保留为词头；
//   5. 由入选词头的 exchange 建 词形 -> 原形 表（examples -> example，went -> go），
//      再用没入选的变形条目自己的 "0:" 项补漏（were -> be）；词形本身是词头的不收；
//   6. 写 SQLite，VACUUM，回读自检，打印各项计数。
// 同样的 CSV + 同样的参数，得到的文件逐字节相同（不写时间戳）。
//
// 查词的规则（App 照此实现）：键 = 去空白、小写、弯撇号换直撇号；先查 words，没有再查 forms 得到原形，再查 words。

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DictionaryBuilder;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private const string Usage = """
        用法：
          build  <ecdict.csv> <输出.db> --commit <SHA> [--rank N] [--page-size 4096] [--audit <目录>]
          lookup <词典.db> <单词>...
        """;

    /// <summary>
    /// 词频名次阈值的默认值：frq 或 bnc 名次在这以内的词入选。
    /// 对照（含 oxford / collins / tag 命中的词，降级之后）：N = 25000 约 3.1 万词，30000 约 3.7 万，35000 约 4.2 万，40000 约 4.8 万。
    /// </summary>
    private const int DefaultRankLimit = 35000;

    internal static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            switch (args)
            {
                case ["build", .. var rest]:
                    return Build(BuildOptions.Parse(rest));
                case ["lookup", var dbPath, .. { Length: > 0 } words]:
                    return Lookup(dbPath, words);
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or SqliteException)
        {
            Console.Error.WriteLine($"出错：{ex.Message}");
            return 1;
        }
    }

    // ---- build ----------------------------------------------------------------

    private static int Build(BuildOptions options)
    {
        var timer = Stopwatch.StartNew();

        Console.WriteLine($"读取 {options.CsvPath}");
        var csvSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.CsvPath))).ToLowerInvariant();

        var builder = new WordListBuilder(options.RankLimit);
        ReadCsv(options.CsvPath, builder);
        var result = builder.Finish();

        var meta = new List<KeyValuePair<string, string>>
        {
            new("source", "https://github.com/skywind3000/ECDICT"),
            new("source_commit", options.Commit),
            new("source_file", "ecdict.csv"),
            new("source_sha256", csvSha256),
            new("license", "MIT"),
            new("author", "Linwei (skywind3000)"),
            new("rank_threshold", options.RankLimit.ToString(CultureInfo.InvariantCulture)),
            new("word_count", result.Words.Count.ToString(CultureInfo.InvariantCulture)),
            new("form_count", result.Forms.Count.ToString(CultureInfo.InvariantCulture)),
        };
        meta.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        DictionaryDb.Write(options.OutputPath, result.Words, result.Forms, meta, options.PageSize);

        PrintReport(builder, result, options, csvSha256);
        SelfCheck(options.OutputPath, result);
        if (options.AuditDir is not null) WriteAudit(options.AuditDir, result);

        var size = new FileInfo(options.OutputPath).Length;
        Console.WriteLine();
        Console.WriteLine($"已写出 {options.OutputPath}：{size:N0} 字节（{size / 1048576.0:F2} MB），用时 {timer.Elapsed.TotalSeconds:F1} 秒");
        return 0;
    }

    private static void ReadCsv(string path, WordListBuilder builder)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);

        EcdictColumns? columns = null;
        var order = 0; // 数据行序号，从 0 开始
        foreach (var row in CsvReader.ReadRows(reader))
        {
            if (columns is null)
            {
                columns = new EcdictColumns(row); // 第一行是表头
                continue;
            }

            if (row.Count != columns.Count)
                throw new InvalidDataException($"第 {order + 1} 条数据记录有 {row.Count} 列，表头是 {columns.Count} 列，CSV 可能不完整");

            builder.Stats.Rows++;
            switch (EcdictRow.TryParse(row, columns, order++, out var entry))
            {
                case RowVerdict.Ok: builder.Add(entry!); break;
                case RowVerdict.NotSingleWord: builder.Stats.NotSingleWord++; break;
                case RowVerdict.EmptyTranslation: builder.Stats.EmptyTranslation++; break;
                case RowVerdict.NoChineseTranslation: builder.Stats.NoChineseTranslation++; break;
            }
        }
    }

    // ---- 报告 ----------------------------------------------------------------

    private static void PrintReport(WordListBuilder builder, BuildResult result, BuildOptions options, string csvSha256)
    {
        var s = result.Stats;
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        void Line(string label, int n, string note = "") => Console.WriteLine($"  {label,-44}{N(n),10}  {note}".TrimEnd());

        Console.WriteLine();
        Console.WriteLine($"CSV sha256: {csvSha256}");
        Console.WriteLine($"ECDICT 提交: {options.Commit}    词频名次阈值 N = {N(options.RankLimit)}");
        Console.WriteLine();
        Console.WriteLine("== 过滤 ==");
        Line("CSV 数据行", s.Rows);
        Line("不是单个词（空格/数字/符号等）", s.NotSingleWord);
        Line("单个词但 translation 为空", s.EmptyTranslation);
        Line("单个词但 translation 里没有汉字", s.NoChineseTranslation);
        Line("候选（单个词 + 有中文释义）", s.Eligible);

        Console.WriteLine();
        Console.WriteLine("== 入选条件（在候选上统计，互相重叠）==");
        Line("oxford = 1", s.Oxford, $"只靠它入选 {N(s.OnlyOxford)}");
        Line("collins >= 1", s.Collins, $"只靠它入选 {N(s.OnlyCollins)}");
        Line("考试 tag 非空", s.ExamTag, $"只靠它入选 {N(s.OnlyExamTag)}");
        Line($"词频名次 <= {N(options.RankLimit)}（frq 或 bnc）", s.Frequency, $"只靠它入选 {N(s.OnlyFrequency)}");
        Line("命中任一条件 = 入选", s.Selected);

        Console.WriteLine();
        Console.WriteLine("== 大小写冲突与变形降级 ==");
        Line("候选里折叠后同键的键数（参考）", s.EligibleCollisionKeys);
        Line("入选里折叠后同键的键数（要裁决的）", s.CollisionKeys,
            $"丢掉 {N(s.CollisionEntriesDropped)} 条；全小写胜 {N(s.CollisionLowercaseWon)}，词频胜 {N(s.CollisionFrequencyWon)}");
        Line("纯变形条目降级为词形（不当词头）", s.Demoted);
        Line("像纯变形条目但释义原形里没有，保留为词头", s.StubsKeptUncovered);

        Console.WriteLine();
        Console.WriteLine("== 结果 ==");
        Line("词头 words", s.Words);
        Line("词形 forms", s.Forms);
        Line("  其中来自降级条目", s.FormsFromNotes);
        Line("  其中只有变形条目自己的 0: 项认领", s.FormsOnlyFromPointers);
        Line("  多个原形认领、按规则选一个", s.FormsAmbiguous);
        Line("因词形本身是词头而没收的词形", s.FormsSkippedHeadword);

        // 最终词头的构成
        var hits = new Dictionary<Criteria, int>
        {
            [Criteria.Oxford] = 0, [Criteria.Collins] = 0, [Criteria.ExamTag] = 0, [Criteria.Frequency] = 0,
        };
        int withPhonetic = 0, withTag = 0, totalLines = 0, maxLines = 0;
        long totalBytes = 0;
        var maxBytes = 0;
        foreach (var e in result.Words)
        {
            var criteria = builder.Evaluate(e);
            foreach (var key in hits.Keys.ToList())
            {
                if (criteria.HasFlag(key)) hits[key]++;
            }
            if (e.Phonetic.Length > 0) withPhonetic++;
            if (e.Tag.Length > 0) withTag++;

            var bytes = Encoding.UTF8.GetByteCount(e.Translation);
            var lines = e.Translation.Count(c => c == '\n') + 1;
            totalBytes += bytes;
            totalLines += lines;
            maxBytes = Math.Max(maxBytes, bytes);
            maxLines = Math.Max(maxLines, lines);
        }

        var words = Math.Max(1, result.Words.Count);
        Console.WriteLine();
        Console.WriteLine("== 最终词头的构成（互相重叠）==");
        Line("oxford = 1", hits[Criteria.Oxford]);
        Line("collins >= 1", hits[Criteria.Collins]);
        Line("考试 tag 非空", hits[Criteria.ExamTag]);
        Line("词频名次在阈值内", hits[Criteria.Frequency]);
        Line("有音标", withPhonetic, $"{100.0 * withPhonetic / words:F1}%");
        Line("有考试 tag", withTag, $"{100.0 * withTag / words:F1}%");
        Console.WriteLine($"  释义 UTF-8 字节：平均 {totalBytes / (double)words:F0}，最长 {maxBytes}；行数：平均 {totalLines / (double)words:F1}，最多 {maxLines}");
    }

    /// <summary>回读刚写的文件，核对数量、完整性和两张表之间的一致性。</summary>
    private static void SelfCheck(string dbPath, BuildResult result)
    {
        using var db = DictionaryDb.Open(dbPath);

        long Scalar(string sql)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        void Expect(string what, long actual, long expected)
        {
            if (actual != expected) throw new InvalidDataException($"自检失败：{what} 应为 {expected}，实际 {actual}");
        }

        Expect("words 行数", Scalar("SELECT COUNT(*) FROM words"), result.Words.Count);
        Expect("forms 行数", Scalar("SELECT COUNT(*) FROM forms"), result.Forms.Count);
        Expect("user_version", Scalar("PRAGMA user_version"), DictionaryDb.SchemaVersion);
        Expect("词形同时是词头的行数", Scalar("SELECT COUNT(*) FROM forms f JOIN words w ON w.word = f.form"), 0);
        Expect("原形不在词头里的词形行数", Scalar("SELECT COUNT(*) FROM forms f LEFT JOIN words w ON w.word = f.lemma WHERE w.word IS NULL"), 0);
        Expect("释义为空的词头行数", Scalar("SELECT COUNT(*) FROM words WHERE translation = ''"), 0);
        Expect("键含大写的词头行数", Scalar("SELECT COUNT(*) FROM words WHERE word <> lower(word)"), 0);

        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        var integrity = (string?)cmd.ExecuteScalar();
        if (integrity != "ok") throw new InvalidDataException($"自检失败：integrity_check = {integrity}");

        Console.WriteLine();
        Console.WriteLine("自检通过：行数、integrity_check、词形/词头互不重叠、原形都在词头里。");
    }

    /// <summary>把关键的取舍写成 TSV，方便人工抽查规则有没有误伤。</summary>
    private static void WriteAudit(string dir, BuildResult result)
    {
        Directory.CreateDirectory(dir);
        static string OneLine(string s) => s.Replace("\n", " / ");

        File.WriteAllLines(Path.Combine(dir, "collisions.tsv"),
            result.Collisions.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => $"{c.Key}\t{c.Kept}\t{c.Reason}\t{string.Join(" | ", c.Dropped)}"));

        File.WriteAllLines(Path.Combine(dir, "demoted.tsv"),
            result.Demotions.OrderBy(d => d.Form, StringComparer.Ordinal)
                .Select(d => $"{d.Form}\t{d.Lemma}\t{OneLine(d.Translation)}"));

        File.WriteAllLines(Path.Combine(dir, "kept-stubs.tsv"),
            result.KeptStubs.OrderBy(k => k.Form, StringComparer.Ordinal)
                .Select(k => $"{k.Form}\t{k.Lemma}\t{OneLine(k.Translation)}"));

        File.WriteAllLines(Path.Combine(dir, "ambiguous-forms.tsv"),
            result.AmbiguousForms.OrderBy(a => a.Form, StringComparer.Ordinal)
                .Select(a => $"{a.Form}\t{a.Chosen}\t{string.Join(" | ", a.Others)}"));

        File.WriteAllLines(Path.Combine(dir, "pointer-only-forms.tsv"),
            result.PointerOnlyForms.OrderBy(p => p.Form, StringComparer.Ordinal)
                .Select(p => $"{p.Form}\t{p.Lemma}"));

        Console.WriteLine($"审计文件已写到 {dir}（collisions / demoted / kept-stubs / ambiguous-forms / pointer-only-forms）");
    }

    // ---- lookup ----------------------------------------------------------------

    private static int Lookup(string dbPath, string[] words)
    {
        if (!File.Exists(dbPath)) throw new FileNotFoundException($"找不到词典文件 {dbPath}");

        using var db = DictionaryDb.Open(dbPath);
        foreach (var word in words)
        {
            var hit = DictionaryDb.Lookup(db, word);
            if (hit is null)
            {
                Console.WriteLine($"{word}  ->  （未收录）");
                Console.WriteLine();
                continue;
            }

            var via = hit.ViaForm is null ? "" : $"   [{hit.ViaForm} 是 {hit.Word} 的词形]";
            Console.WriteLine($"{word}  ->  {hit.Word}{via}");
            Console.WriteLine($"  音标: {hit.Phonetic ?? "（无）"}    标签: {hit.Tag ?? "（无）"}");
            foreach (var line in hit.Translation.Split('\n')) Console.WriteLine($"  {line}");
            Console.WriteLine();
        }
        return 0;
    }

    // ---- 命令行参数 ----------------------------------------------------------------

    private sealed record BuildOptions(string CsvPath, string OutputPath, string Commit, int RankLimit, int PageSize, string? AuditDir)
    {
        public static BuildOptions Parse(string[] args)
        {
            var positional = new List<string>();
            string commit = "unknown";
            string? auditDir = null;
            var rankLimit = DefaultRankLimit;
            var pageSize = 4096;

            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} 后面缺少参数");
                switch (args[i])
                {
                    case "--commit": commit = Value(); break;
                    case "--rank": rankLimit = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--page-size": pageSize = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--audit": auditDir = Value(); break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"不认识的选项 {args[i]}");
                        positional.Add(args[i]);
                        break;
                }
            }

            if (positional.Count != 2) throw new ArgumentException(Usage);
            if (!File.Exists(positional[0])) throw new FileNotFoundException($"找不到 CSV 文件 {positional[0]}");
            if (commit == "unknown") Console.Error.WriteLine("提示：没有给 --commit，meta 里的来源提交会记成 unknown。");

            return new BuildOptions(positional[0], positional[1], commit, rankLimit, pageSize, auditDir);
        }
    }
}
