using Shiyu.Core;

namespace Shiyu.Core.Tests.Fakes;

/// <summary>
/// A clipboard and keyboard the test drives by hand, so the capture timing can
/// be examined without a real selection in a real application.
///
/// The clipboard holds what a real one can: text with its HTML and RTF forms,
/// a file list, formats that cannot be written back (an image), or a password
/// manager's "do not record" marker (用户实录 2026-10-15：拖选取词原来只借走
/// 文字，图片被清空、密码被去掉标记写回).
/// </summary>
public sealed class FakeCapturePlatform : ICapturePlatform
{
    private string? _clipboard;
    private string? _html;
    private string? _rtf;
    private IReadOnlyList<string> _files = [];
    private IReadOnlyList<string> _lost = [];
    private bool _excluded;
    private bool _ours;
    private uint _sequence;
    private int _waits;

    /// <summary>How many polls the target takes to answer. Null means never.</summary>
    public int? AnswersAfterPolls { get; set; }

    /// <summary>What the target puts on the clipboard when it answers.</summary>
    public string? Answer { get; set; } = "the selected text";

    public bool ReadFails { get; set; }
    public bool WriteFails { get; set; }
    public bool WriteThrows { get; set; }

    /// <summary>还原撞上"剪贴板被别的程序占着"的次数：前这么多次写回都没写上。</summary>
    public int RestoreBusyTimes { get; set; }

    public List<string?> Writes { get; } = [];

    /// <summary>真正写回去的快照，按先后。</summary>
    public List<ClipboardBackup> Restores { get; } = [];

    public int CopyKeystrokes { get; private set; }
    public int PasteKeystrokes { get; private set; }
    public int Polls { get; private set; }

    /// <summary>先后发生的事：wait、read、write、backup、restore、copy、paste——给需要断言次序的测试看。</summary>
    public List<string> Events { get; } = [];

    public void PutOnClipboard(string? text) => Put(text);

    /// <summary>带格式的文字：浏览器、Office 复制出来的那种。</summary>
    public void PutRich(string text, string html, string rtf) => Put(text, html: html, rtf: rtf);

    public void PutFiles(params string[] paths) => Put(null, files: paths);

    /// <summary>一张图（截图之类）：拾语的写入器写不回去的格式。</summary>
    public void PutImage() => Put(null, lost: ["CF_DIB", "PNG"]);

    /// <summary>从 Word、Excel 复制的文字：带格式，还附着写不回去的图元文件、位图。</summary>
    public void PutOffice(string text, string html, string rtf)
        => Put(text, html: html, rtf: rtf, lost: ["CF_ENHMETAFILE", "CF_BITMAP"]);

    /// <summary>资源管理器里剪切的文件：写回去"移动"就成了"复制"。</summary>
    public void PutCutFiles(params string[] paths)
        => Put(null, files: paths, lost: [ClipboardFormats.CutDropEffect]);

    /// <summary>密码管理器复制的密码：带「勿记录」标记。</summary>
    public void PutExcluded(string text) => Put(text, excluded: true);

    private void Put(
        string? text,
        string? html = null,
        string? rtf = null,
        IReadOnlyList<string>? files = null,
        IReadOnlyList<string>? lost = null,
        bool excluded = false)
    {
        _clipboard = text;
        _html = html;
        _rtf = rtf;
        _files = files ?? [];
        _lost = lost ?? [];
        _excluded = excluded;
        _sequence++;

        // 默认是别人（用户、被复制的应用）写的；拾语自己的写入在 Write/Restore 里改回来。
        _ours = false;
    }

    public bool OwnsClipboard() => _ours;

    public string? CurrentClipboard => _clipboard;

    public string? CurrentHtml => _html;

    public string? CurrentRtf => _rtf;

    public IReadOnlyList<string> CurrentFiles => _files;

    /// <summary>剪贴板上还有写不回去的格式（图片、图元文件、剪切标记……）。</summary>
    public bool CurrentlyHoldsImage => _lost.Count > 0;

    public uint ClipboardSequenceNumber()
    {
        Polls++;
        return _sequence;
    }

    public string? ReadClipboardText()
    {
        Events.Add("read");
        return ReadFails ? throw new ClipboardUnavailableException("read failed") : _clipboard;
    }

    public bool WriteClipboardText(string? text)
    {
        if (WriteThrows)
        {
            throw new ClipboardUnavailableException("write failed");
        }

        Events.Add("write");
        Writes.Add(text);
        if (WriteFails)
        {
            return false;
        }

        Put(text);
        _ours = true;
        return true;
    }

    public ClipboardBackup? Backup()
    {
        Events.Add("backup");
        if (ReadFails)
        {
            return null;
        }

        if (_excluded)
        {
            // 与 Windows 层一致：带标记的快照不读内容。
            return new ClipboardBackup(null, null, null, [], Excluded: true, LostFormats: [], Empty: false);
        }

        var empty = _clipboard is null && _html is null && _rtf is null && _files.Count == 0 && _lost.Count == 0;
        return new ClipboardBackup(_clipboard, _html, _rtf, _files, Excluded: false, LostFormats: _lost, Empty: empty);
    }

    public bool Restore(ClipboardBackup backup)
    {
        if (WriteThrows)
        {
            throw new ClipboardUnavailableException("write failed");
        }

        if (RestoreBusyTimes > 0)
        {
            RestoreBusyTimes--;
            Events.Add("restore-busy");
            return false;
        }

        Events.Add("restore");
        if (WriteFails)
        {
            return false;
        }

        Restores.Add(backup);
        Put(backup.Text, backup.Html, backup.Rtf, backup.Files);
        _ours = true;
        return true;
    }

    /// <summary>用户的手指在修饰键上停留多少次查询（热键的 Ctrl、Shift……）；0 = 一开始就没按着。</summary>
    public int ModifiersHeldForChecks { get; set; }

    public int ModifierChecks { get; private set; }

    /// <summary>第一次发键之前等过几回（-1 = 还没发过键）。</summary>
    public int WaitsBeforeFirstKeystroke { get; private set; } = -1;

    /// <summary>第一次发键之前写过几回剪贴板（-1 = 还没发过键）。</summary>
    public int WritesBeforeFirstKeystroke { get; private set; } = -1;

    public bool ModifiersHeld() => ModifierChecks++ < ModifiersHeldForChecks;

    public void SendCopyKeystroke()
    {
        NoteKeystroke();
        Events.Add("copy");
        CopyKeystrokes++;
    }

    public void SendPasteKeystroke()
    {
        NoteKeystroke();
        Events.Add("paste");
        PasteKeystrokes++;
    }

    private void NoteKeystroke()
    {
        if (WaitsBeforeFirstKeystroke < 0)
        {
            WaitsBeforeFirstKeystroke = _waits;
            WritesBeforeFirstKeystroke = Writes.Count;
        }
    }

    public void Wait(TimeSpan duration)
    {
        Events.Add("wait");
        _waits++;

        // The target application "responds" once the agreed number of polls has
        // gone by, which is how a slow application is simulated. Its copy
        // replaces every format the user had.
        if (AnswersAfterPolls is { } threshold && _waits == threshold)
        {
            Put(Answer);
        }
    }
}
