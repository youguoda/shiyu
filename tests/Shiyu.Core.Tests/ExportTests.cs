using System.Text;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 导出（评审 2026-10-16 F-19）：剪贴板历史与翻译记录导出 CSV / Markdown，内容与条目一致；图片只有占位与
/// 文件名；CSV 防公式注入；整个历史一页一页地读。
/// </summary>
public class ExportTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static string Csv(IEnumerable<Entry> entries)
    {
        var writer = new StringWriter();
        EntryExport.Write(writer, entries, ExportFormat.Csv, TimeZoneInfo.Utc);
        return writer.ToString();
    }

    private static string Markdown(IEnumerable<Entry> entries)
    {
        var writer = new StringWriter();
        EntryExport.Write(writer, entries, ExportFormat.Markdown, TimeZoneInfo.Utc);
        return writer.ToString();
    }

    [Fact]
    public void Three_entries_export_as_a_header_and_three_rows()
    {
        var csv = Csv(
        [
            new Entry(1, "first", "Chrome", Noon) { Note = "备注一", Tags = ["工作", "待办"], Favorite = true },
            new Entry(2, "second, with a comma", "Code", Noon.AddMinutes(1)) { IsPinned = true },
            new Entry(3, "third \"quoted\"\nline two", null, Noon.AddMinutes(2)),
        ]);

        Assert.Equal(
            "时间,类型,来源应用,内容,备注,标签,收藏,置顶\r\n"
            + "2026-10-16 12:00:00,文本,Chrome,first,备注一,工作、待办,是,\r\n"
            + "2026-10-16 12:01:00,文本,Code,\"second, with a comma\",,,,是\r\n"
            + "2026-10-16 12:02:00,文本,,\"third \"\"quoted\"\"\nline two\",,,,\r\n",
            csv);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+1+2")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void A_cell_that_a_spreadsheet_would_run_as_a_formula_is_defused(string content)
    {
        var csv = Csv([new Entry(1, content, "test", Noon)]);

        var row = csv.Split("\r\n")[1];
        Assert.Contains("'" + content[0], row);
        Assert.DoesNotContain("," + content[0], row);
    }

    [Fact]
    public void Images_export_a_placeholder_and_the_file_name_never_the_bytes()
    {
        var kept = new Entry(1, "图片 640×480", "Snip", Noon)
        {
            Kind = EntryKind.Image,
            OriginalPath = @"C:\data\images\20261016-120000-abcdef.png",
            ImageWidth = 640,
            ImageHeight = 480,
            ThumbnailPng = [1, 2, 3],
        };
        var swept = kept with { Id = 2, OriginalPath = null };

        var csv = Csv([kept, swept]);

        Assert.Contains("[图片 640×480] 20261016-120000-abcdef.png", csv);
        Assert.Contains("[图片 640×480]（原图已按保留期清理）", csv);
        Assert.DoesNotContain(@"C:\data", csv);
    }

    [Fact]
    public void Files_export_their_paths()
    {
        var files = new Entry(1, "a.txt 等 2 项", "Explorer", Noon)
        {
            Kind = EntryKind.Files,
            Files = [@"C:\docs\a.txt", @"C:\docs\b.txt"],
        };

        Assert.Contains("\"C:\\docs\\a.txt\nC:\\docs\\b.txt\"", Csv([files]));
        Assert.Contains("- C:\\docs\\a.txt\n- C:\\docs\\b.txt\n", Markdown([files]));
    }

    [Fact]
    public void Markdown_keeps_each_entry_verbatim_inside_a_fence_longer_than_its_backticks()
    {
        var md = Markdown(
        [
            new Entry(1, "# not a heading\n```js\ncode\n```", "Code", Noon) { Note = "看这个", Tags = ["示例"] },
        ]);

        Assert.Equal(
            "## 2026-10-16 12:00:00 · 文本 · 来自 Code\n\n"
            + "> 备注：看这个\n\n"
            + "标签：示例\n\n"
            + "````\n# not a heading\n```js\ncode\n```\n````\n\n",
            md);
    }

    [Fact]
    public void The_whole_history_is_read_page_by_page_without_repeats_or_gaps()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        for (var i = 0; i < 25; i++)
        {
            store.Append($"entry {i}", "test", Noon.AddMinutes(i));
        }

        var pinned = store.Append("pinned", "test", Noon.AddMinutes(-60));
        store.SetPinned(pinned.Id, true);

        var all = EntryExport.All(store, pageSize: 10).ToList();

        Assert.Equal(26, all.Count);
        Assert.Equal(26, all.Select(entry => entry.Id).Distinct().Count());
        Assert.Equal("pinned", all[0].Text);
        Assert.Equal("entry 24", all[1].Text);
    }

    [Fact]
    public void Translation_records_export_both_sides()
    {
        var records = new[]
        {
            new TranslationRecord(1, "hello, world", "你好，世界", TranslationOrigin.Panel, "标准", Noon),
            new TranslationRecord(2, "我要请假", "I'd like to take a day off.", TranslationOrigin.ReverseInput, "正式", Noon.AddMinutes(5)),
        };

        var csv = new StringWriter();
        Assert.Equal(2, TranslationLogExport.Write(csv, records, ExportFormat.Csv, TimeZoneInfo.Utc));
        Assert.Equal(
            "时间,来自,模板,原文,译文\r\n"
            + "2026-10-16 12:00:00,翻译框,标准,\"hello, world\",你好，世界\r\n"
            + "2026-10-16 12:05:00,反向输入框,正式,我要请假,I'd like to take a day off.\r\n",
            csv.ToString());

        var md = new StringWriter();
        TranslationLogExport.Write(md, records.Take(1), ExportFormat.Markdown, TimeZoneInfo.Utc);
        Assert.Equal(
            "## 2026-10-16 12:00:00 · 翻译框 · 标准\n\n**原文**\n\n```\nhello, world\n```\n\n**译文**\n\n```\n你好，世界\n```\n\n",
            md.ToString());
    }

    [Fact]
    public void Csv_files_carry_a_bom_so_spreadsheets_read_chinese_and_markdown_does_not()
    {
        Assert.Equal([0xEF, 0xBB, 0xBF], ExportFile.EncodingFor(ExportFormat.Csv).GetPreamble());
        Assert.Empty(ExportFile.EncodingFor(ExportFormat.Markdown).GetPreamble());
        Assert.Equal(ExportFormat.Markdown, ExportFile.FormatAt(2));
        Assert.Equal(ExportFormat.Csv, ExportFile.FormatAt(1));
    }
}
