using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// 反向输入框回贴时对剪贴板的操作（票 43）——<see cref="IReversePasteClipboard"/> 的 Win32 实现。
///
/// <b>写入与还原都经 <see cref="WindowsClipboardWriter"/></b>（拾语作为剪贴板 owner）：
/// <see cref="WindowsClipboardMonitor"/> 按 owner 跳过它们，所以译文与被还原的原内容
/// 都不产生历史条目、不弹徽标——这是运输，不是复制。这里不直接碰 SetClipboardData/
/// EmptyClipboard，Core 的 ClipboardWriteGateTests 从结构上钉住这一点。
///
/// 快照只做读：枚举格式名（不会让所有者去渲染延迟格式——Excel 的图片、整张表不会被白白渲染一遍），
/// 只读写得回去的那几种内容（文本、HTML、RTF、文件列表）。带排除标记的快照<b>连内容都不读</b>。
/// </summary>
public sealed class WindowsReversePasteClipboard(MessageWindow window, WindowsClipboardWriter writer)
    : IReversePasteClipboard
{
    private const int OpenAttempts = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(15);

    /// <summary>"注册格式"从这个 id 起（0xC000–0xFFFF）：只有它们有名字可查。</summary>
    private const uint FirstRegisteredFormat = 0xC000;

    private static readonly uint HtmlFormat = NativeMethods.RegisterClipboardFormatW("HTML Format");
    private static readonly uint RtfFormat = NativeMethods.RegisterClipboardFormatW("Rich Text Format");

    private static readonly uint PreferredDropEffectFormat =
        NativeMethods.RegisterClipboardFormatW("Preferred DropEffect");

    public ClipboardBackup? Backup()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (!NativeMethods.OpenClipboard(window.Handle))
            {
                Thread.Sleep(RetryDelay);
                continue;
            }

            try
            {
                return ReadOpenClipboard();
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        // 别的进程一直占着：读不了。回贴照常进行，只是没有东西可还。
        return null;
    }

    public bool Write(string text) => writer.SetText(text);

    public uint SequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    public bool Restore(ClipboardBackup backup)
    {
        if (backup.Files.Count > 0)
        {
            return writer.SetFiles(backup.Files);
        }

        if (backup.Text is { } text)
        {
            return writer.SetRich(text, backup.Html, backup.Rtf);
        }

        // 原来就是空的：还原就是清空，而不是写一个空字符串。
        return backup.Empty && writer.Clear();
    }

    /// <summary>要求剪贴板已打开。</summary>
    private static ClipboardBackup ReadOpenClipboard()
    {
        var formats = EnumerateFormats();
        if (formats.Count == 0)
        {
            return new ClipboardBackup(null, null, null, [], Excluded: false, LostFormats: [], Empty: true);
        }

        // 排除标记先于一切：密码管理器复制出来的东西，连在内存里多留一份都没有必要。
        if (WindowsClipboardMonitor.IsExcludedByMarker())
        {
            return new ClipboardBackup(null, null, null, [], Excluded: true, LostFormats: [], Empty: false);
        }

        bool Has(uint format) => formats.Any(entry => entry.Id == format);

        var lost = ClipboardFormats.LostAmong(formats).ToList();
        var files = Has(NativeMethods.CfHdrop) ? WindowsClipboardMonitor.ReadFileDrop() : [];

        // 剪切（"Preferred DropEffect" 带着移动）：只写回文件列表会把"移动"悄悄变成"复制"，
        // 所以按写不回去算。
        if (files.Count > 0
            && Has(PreferredDropEffectFormat)
            && WindowsClipboardMonitor.ReadDword(PreferredDropEffectFormat) is { } effect
            && ClipboardFormats.IsMoveDropEffect(effect))
        {
            lost.Add("Preferred DropEffect (move)");
        }

        return new ClipboardBackup(
            Has(NativeMethods.CfUnicodeText) ? WindowsClipboardMonitor.ReadUnicodeText() : null,
            Has(HtmlFormat) ? WindowsClipboardMonitor.ReadFormatted() : null,
            Has(RtfFormat) ? WindowsClipboardMonitor.ReadString(RtfFormat) : null,
            files,
            Excluded: false,
            LostFormats: lost,
            Empty: false);
    }

    /// <summary>剪贴板上现有的格式：标准格式只有 id，注册格式带上名字。要求剪贴板已打开。</summary>
    private static List<(uint Id, string? Name)> EnumerateFormats()
    {
        var formats = new List<(uint Id, string? Name)>();

        for (var format = NativeMethods.EnumClipboardFormats(0);
             format != 0;
             format = NativeMethods.EnumClipboardFormats(format))
        {
            formats.Add((format, format >= FirstRegisteredFormat ? NameOf(format) : null));
        }

        return formats;
    }

    private static string? NameOf(uint format)
    {
        var name = new System.Text.StringBuilder(capacity: 256);
        var length = NativeMethods.GetClipboardFormatNameW(format, name, name.Capacity);
        return length > 0 ? name.ToString(0, length) : null;
    }
}
