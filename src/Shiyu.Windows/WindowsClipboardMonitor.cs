using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// Turns Windows clipboard notifications into <see cref="ClipboardSnapshot"/>.
///
/// Uses <c>AddClipboardFormatListener</c>, the modern notification mechanism —
/// not the legacy <c>SetClipboardViewer</c> chain, where one badly behaved
/// application can break notifications for everyone downstream of it.
/// </summary>
public sealed class WindowsClipboardMonitor : IClipboardMonitor, IDisposable
{
    private const int OpenAttempts = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// The registered formats an application uses to ask that its clipboard
    /// content be left out of clipboard history. Documented under "Cloud
    /// Clipboard and Clipboard History Formats"; honouring them is a
    /// convention rather than something Windows enforces, so it is Shiyu's
    /// own decision to respect them — and with an unencrypted history, not
    /// respecting them is not a defensible option.
    /// </summary>
    private static readonly uint ExcludeFromMonitorsFormat =
        NativeMethods.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");

    private static readonly uint CanIncludeInHistoryFormat =
        NativeMethods.RegisterClipboardFormatW("CanIncludeInClipboardHistory");

    // CanUploadToCloudClipboard is deliberately not consulted: it governs
    // synchronisation to the user's other devices, which Shiyu never does.
    // Treating it as a request for local secrecy would punch holes in the
    // history for content the user has every reason to expect to find there.

    private readonly MessageWindow _window;
    private bool _listening;
    private bool _disposed;

    /// <summary>
    /// 读不了时隔多久再读（用户日志 2026-10-13 FatalExit）。Windows 剪贴板历史、输入法、别的剪贴板
    /// 工具在每次变化后都抢着读剪贴板，偶尔一占就超过上面 8 回、一百多毫秒的耐心；原来读不了就拿
    /// 空值往下走，整个程序崩掉。现在隔一会儿再读——剪贴板若在那之前又变了，新的通知自会接手。
    /// 挂在消息窗的 WM_TIMER 上，回到同一条线程。
    /// </summary>
    private static readonly uint[] RereadDelaysMs = [100, 300, 800];

    private static readonly IntPtr RereadTimer = new(0x5359);

    /// <summary>还没读到的那次变化：读到时仍记在当时的前台应用名下。</summary>
    private PendingRead? _pending;

    private int _rereads;

    private static readonly uint HtmlFormat =
        NativeMethods.RegisterClipboardFormatW("HTML Format");

    private static readonly uint RtfFormat =
        NativeMethods.RegisterClipboardFormatW("Rich Text Format");

    public event Action<ClipboardSnapshot>? Changed;

    public WindowsClipboardMonitor(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
        _listening = NativeMethods.AddClipboardFormatListener(_window.Handle);

        if (!_listening)
        {
            throw new InvalidOperationException(
                "Could not subscribe to clipboard notifications.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private void OnMessage(WindowMessage message)
    {
        if (message.Id == NativeMethods.WmTimer && message.WParam == RereadTimer)
        {
            message.Handle();
            NativeMethods.KillTimer(_window.Handle, RereadTimer);
            Reread();
            return;
        }

        if (message.Id != NativeMethods.WmClipboardUpdate)
        {
            return;
        }

        message.Handle();

        // Self-suppression. Shiyu writes to the clipboard itself — re-copying
        // an entry from the library, and restoring what a capture borrowed —
        // and each of those writes comes back as a change notification. Owning
        // the clipboard is the exact test for "this one was mine", so Shiyu
        // never records its own hand.
        if (NativeMethods.GetClipboardOwner() == _window.Handle)
        {
            return;
        }

        // The foreground window is read first: opening the clipboard can take
        // several attempts, by which time focus may have moved on.
        var (sourceApp, sourceExe) = ForegroundProcess();
        _rereads = 0;
        Publish(sourceApp, sourceExe);
    }

    /// <summary>
    /// 读一回剪贴板，把值得记的那份交出去。读不了（别的程序一直占着）就约下一回，见
    /// <see cref="RereadDelaysMs"/>。
    /// </summary>
    private void Publish(string? sourceApp, string? sourceExe)
    {
        if (ReadClipboard() is not { } reading)
        {
            ScheduleReread(sourceApp, sourceExe);
            return;
        }

        if (reading.Text.Length > 0)
        {
            Changed?.Invoke(new ClipboardSnapshot(reading.Text, sourceApp, reading.Excluded)
            {
                SourceExePath = sourceExe,
                Html = reading.Html,
                Rtf = reading.Rtf,
            });
            return;
        }

        // No usable text. Files are the next thing worth keeping: a copy in
        // Explorer publishes CF_HDROP and nothing else. They were read inside
        // the same clipboard open as the text — GetClipboardData answers null
        // on a clipboard that is already closed again.
        if (reading.Files is { Count: > 0 } files)
        {
            Changed?.Invoke(new ClipboardSnapshot(string.Empty, sourceApp, reading.Excluded)
            {
                Files = files,
                SourceExePath = sourceExe,
            });
            return;
        }

        // No usable text. An image is the other thing worth keeping, and is
        // grabbed now rather than later: the clipboard is about to change again
        // and there is no second chance at it. Only the raw bytes were taken,
        // inside the same open — decoding and the repeat-collapsing fingerprint
        // happen on the pipeline's thread (O-36); the duplicate publications
        // of one copy are collapsed there. The marker applies to the whole
        // clipboard, images included: an image copied from a password manager
        // must not be recorded either.
        if (reading.Image is { } image)
        {
            Changed?.Invoke(new ClipboardSnapshot(string.Empty, sourceApp, reading.Excluded)
            {
                Image = image,
                SourceExePath = sourceExe,
            });
        }
    }

    private void ScheduleReread(string? sourceApp, string? sourceExe)
    {
        if (_rereads >= RereadDelaysMs.Length)
        {
            // 试满了：这一次复制没记上。不值得打扰用户，更不是崩溃的理由——下一次复制照常记。
            _pending = null;
            Log.Event(LogEvent.ClipboardReadMissed, ("rereads", _rereads));
            return;
        }

        _pending = new PendingRead(sourceApp, sourceExe, NativeMethods.GetClipboardSequenceNumber());
        NativeMethods.SetTimer(_window.Handle, RereadTimer, RereadDelaysMs[_rereads++], IntPtr.Zero);
    }

    private void Reread()
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;

        // 剪贴板在这期间又变了：那次变化有它自己的通知，这一份已经过时。
        if (NativeMethods.GetClipboardSequenceNumber() != pending.Sequence)
        {
            return;
        }

        Publish(pending.SourceApp, pending.SourceExe);
    }

    private readonly record struct PendingRead(string? SourceApp, string? SourceExe, uint Sequence);

    private readonly record struct Reading(
        string Text,
        bool Excluded,
        string? Html,
        string? Rtf,
        IReadOnlyList<string> Files,
        WindowsClipboardImage? Image);

    /// <summary>
    /// The CF_HDROP file list, when the clipboard carries one. Requires the
    /// clipboard to already be open.
    /// </summary>
    internal static IReadOnlyList<string> ReadFileDrop()
    {
        var handle = NativeMethods.GetClipboardData(NativeMethods.CfHdrop);
        if (handle == IntPtr.Zero)
        {
            return [];
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            // DROPFILES: DWORD pFiles (offset to the list), POINT pt, BOOL
            // fNC, BOOL fWide — twenty bytes in total, with fWide at 16.
            // Reading the flag from the wrong offset decodes every list as
            // ANSI and turns wide-char paths into single-character garbage.
            var offset = Marshal.ReadInt32(pointer);
            var wide = Marshal.ReadInt32(pointer, 16) != 0;
            var list = pointer + offset;

            var paths = new List<string>();
            var step = 0;

            while (true)
            {
                var path = wide
                    ? Marshal.PtrToStringUni(list + step * 2)
                    : Marshal.PtrToStringAnsi(list + step);
                if (string.IsNullOrEmpty(path))
                {
                    break;
                }

                paths.Add(path);

                // Advances in list units: wide chars for the wide form; for
                // the legacy ANSI form the converted length is the byte count
                // for the paths that still use it (ASCII ones, in practice).
                step += path.Length + 1;
            }

            return paths;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// Reads the text and the exclusion markers in a single open, retrying
    /// while another process holds the clipboard. Only one process may have it
    /// open at a time, so a failure here is ordinary contention, not an error.
    /// </summary>
    private Reading? ReadClipboard()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (!NativeMethods.OpenClipboard(_window.Handle))
            {
                Thread.Sleep(RetryDelay);
                continue;
            }

            try
            {
                var excluded = IsExcludedByMarker();

                // Everything is read inside this one open — text, formats,
                // files, and (when neither text nor files are there) the
                // image's bytes. GetClipboardData answers null once the
                // clipboard is closed, and the file branch used to learn that
                // the hard way; the image branch, moved in here by O-36,
                // avoids learning it twice.

                // Returned even when there is no text, so the marker survives
                // for an image-only clipboard. Dropping the reading here would
                // quietly reopen the hole exclusion exists to close: an image
                // copied from a password manager would be recorded.
                var text = ReadUnicodeText() ?? string.Empty;
                var files = text.Length == 0 ? ReadFileDrop() : [];

                var image = text.Length == 0 && files.Count == 0
                    ? WindowsClipboardImage.FromOpenClipboard()
                    : null;

                return new Reading(
                    text,
                    excluded,
                    ReadFormatted(),
                    ReadString(RtfFormat),
                    files,
                    image);
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return null;
    }

    /// <summary>Requires the clipboard to already be open.</summary>
    internal static bool IsExcludedByMarker()
    {
        if (NativeMethods.IsClipboardFormatAvailable(ExcludeFromMonitorsFormat))
        {
            return true;
        }

        // Documented as a serialized DWORD: zero means keep it out of history,
        // one means the application explicitly wants it kept.
        return ReadDword(CanIncludeInHistoryFormat) == 0;
    }

    /// <summary>Requires the clipboard to already be open.</summary>
    internal static uint? ReadDword(uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format))
        {
            return null;
        }

        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero || (ulong)NativeMethods.GlobalSize(handle) < sizeof(uint))
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return unchecked((uint)Marshal.ReadInt32(pointer));
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>Requires the clipboard to already be open.</summary>
    internal static string? ReadUnicodeText()
    {
        var handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// The copy's HTML fragment, when the source published one. Only the
    /// fragment is kept — the header is transport, not content.
    /// </summary>
    internal static string? ReadFormatted()
    {
        var bytes = ReadBytes(HtmlFormat);
        return bytes is null ? null : ClipboardHtml.ExtractFragment(bytes);
    }

    /// <summary>Requires the clipboard to already be open.</summary>
    internal static string? ReadString(uint format)
    {
        var bytes = ReadBytes(format);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>Requires the clipboard to already be open.</summary>
    private static byte[]? ReadBytes(uint format)
    {
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (int)NativeMethods.GlobalSize(handle);
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// The foreground application's process name and executable path. The path
    /// is taken now — while the process is alive — because that is the only
    /// moment its icon is guaranteed extractable, and an uninstalled
    /// application's history should still show the icon it had.
    /// </summary>
    private static (string? Name, string? ExePath) ForegroundProcess()
        => ForegroundApplication.Current();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.MessageReceived -= OnMessage;
        NativeMethods.KillTimer(_window.Handle, RereadTimer);
        _pending = null;

        if (_listening)
        {
            NativeMethods.RemoveClipboardFormatListener(_window.Handle);
            _listening = false;
        }
    }
}
