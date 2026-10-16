using System.Globalization;
using System.Text;

namespace Shiyu.Core;

public enum ExportFormat
{
    Csv,
    Markdown,
}

/// <summary>
/// 导出（评审 2026-10-16 F-19：备份是私有格式，拿去别处用得有个通用的）：剪贴板历史与翻译记录导出成 CSV
/// 或 Markdown。边读边写，整个历史从不一次装进内存；图片只写占位与原图文件名，不导出二进制。
/// </summary>
public static class EntryExport
{
    /// <summary>整个历史，按列表的次序（置顶在前、新的在前），一页一页地读。</summary>
    public static IEnumerable<Entry> All(EntryStore store, int pageSize = 500)
    {
        PageCursor? cursor = null;
        while (true)
        {
            var page = store.Page(pageSize, cursor);
            foreach (var entry in page)
            {
                yield return entry;
            }

            if (page.Count < pageSize)
            {
                yield break;
            }

            cursor = PageCursor.Of(page[^1]);
        }
    }

    /// <returns>写了几条。</returns>
    public static int Write(TextWriter writer, IEnumerable<Entry> entries, ExportFormat format, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var count = 0;
        if (format == ExportFormat.Csv)
        {
            Csv.Row(writer, "时间", "类型", "来源应用", "内容", "备注", "标签", "收藏", "置顶");
            foreach (var entry in entries)
            {
                Csv.Row(
                    writer,
                    Export.Time(entry.CreatedAt, zone),
                    KindName(entry.Kind),
                    entry.SourceApp ?? string.Empty,
                    Content(entry, "\n"),
                    entry.Note ?? string.Empty,
                    string.Join("、", entry.Tags),
                    entry.Favorite ? "是" : string.Empty,
                    entry.IsPinned ? "是" : string.Empty);
                count++;
            }

            return count;
        }

        foreach (var entry in entries)
        {
            var source = entry.SourceApp is { Length: > 0 } app ? $" · 来自 {app}" : string.Empty;
            writer.Write($"## {Export.Time(entry.CreatedAt, zone)} · {KindName(entry.Kind)}{source}\n\n");
            if (entry.Note is { Length: > 0 } note)
            {
                writer.Write($"> 备注：{note.ReplaceLineEndings(" ")}\n\n");
            }

            if (entry.Tags.Count > 0)
            {
                writer.Write($"标签：{string.Join("、", entry.Tags)}\n\n");
            }

            writer.Write(entry.Kind switch
            {
                EntryKind.Image => $"*{ImagePlaceholder(entry)}*\n\n",
                EntryKind.Files => string.Concat(entry.Files.Select(path => $"- {path}\n")) + "\n",
                _ => Export.Fenced(entry.Text),
            });
            count++;
        }

        return count;
    }

    private static string KindName(EntryKind kind) => kind switch
    {
        EntryKind.Image => "图片",
        EntryKind.Files => "文件",
        _ => "文本",
    };

    private static string Content(Entry entry, string newline) => entry.Kind switch
    {
        EntryKind.Image => ImagePlaceholder(entry),
        EntryKind.Files => string.Join(newline, entry.Files),
        _ => entry.Text,
    };

    /// <summary>图片不导出二进制：占位加原图的文件名，原图已被保留期清掉的如实说。</summary>
    private static string ImagePlaceholder(Entry entry)
        => entry.OriginalPath is { Length: > 0 } path
            ? $"[图片 {entry.ImageWidth}×{entry.ImageHeight}] {Path.GetFileName(path)}"
            : $"[图片 {entry.ImageWidth}×{entry.ImageHeight}]（原图已按保留期清理）";
}

/// <summary>翻译记录的导出（评审 2026-10-16 F-19），格式与剪贴板历史同一套。</summary>
public static class TranslationLogExport
{
    /// <returns>写了几条。</returns>
    public static int Write(
        TextWriter writer, IEnumerable<TranslationRecord> records, ExportFormat format, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var count = 0;
        if (format == ExportFormat.Csv)
        {
            Csv.Row(writer, "时间", "来自", "模板", "原文", "译文");
            foreach (var record in records)
            {
                Csv.Row(
                    writer,
                    Export.Time(record.CreatedAt, zone),
                    TranslationLogText.OriginName(record.Origin),
                    record.Template,
                    record.Original,
                    record.Translated);
                count++;
            }

            return count;
        }

        foreach (var record in records)
        {
            writer.Write(
                $"## {Export.Time(record.CreatedAt, zone)} · {TranslationLogText.OriginName(record.Origin)} · {record.Template}\n\n");
            writer.Write("**原文**\n\n");
            writer.Write(Export.Fenced(record.Original));
            writer.Write("**译文**\n\n");
            writer.Write(Export.Fenced(record.Translated));
            count++;
        }

        return count;
    }
}

/// <summary>导出文件本身：存成什么编码、默认叫什么、对话框里两种格式的过滤器。</summary>
public static class ExportFile
{
    /// <summary>保存对话框的过滤器；选中的序号（从 1 起）对应 <see cref="FormatAt"/>。</summary>
    public const string Filter = "CSV（表格）|*.csv|Markdown（文档）|*.md";

    public static ExportFormat FormatAt(int filterIndex) => filterIndex == 2 ? ExportFormat.Markdown : ExportFormat.Csv;

    /// <summary>CSV 带 BOM：没有它，Excel 按本地代码页读，中文成乱码。Markdown 不带。</summary>
    public static Encoding EncodingFor(ExportFormat format)
        => format == ExportFormat.Csv ? new UTF8Encoding(true) : new UTF8Encoding(false);

    public static string DefaultName(string what, DateTime now)
        => $"拾语{what} {now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

/// <summary>两种导出共用的小件：时间、CSV 的一行、Markdown 的围栏。</summary>
internal static class Export
{
    public static string Time(DateTimeOffset at, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH':'mm':'ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// 原样包进代码围栏：围栏比内容里最长的一串反引号还长一个，内容里写什么都不会提前收口、
    /// 也不会被当成 Markdown 解释。
    /// </summary>
    public static string Fenced(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var ch in text)
        {
            run = ch == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}\n{text.ReplaceLineEndings("\n").TrimEnd('\n')}\n{fence}\n\n";
    }
}

/// <summary>
/// RFC 4180 的一行：含逗号、引号、换行的格子加引号，引号成对。以 = + - @ 打头的格子前面补一个 '：
/// 剪贴板里的东西来自四面八方，表格软件打开时不该把其中一格当公式执行（OWASP 的 CSV 注入建议）。
/// </summary>
internal static class Csv
{
    public static void Row(TextWriter writer, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(Cell(cells[i]));
        }

        writer.Write("\r\n");
    }

    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
