using System.Runtime.InteropServices;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// The Windows side of capture: synthesised keystrokes and the clipboard reads
/// and writes around them. The timing that uses all this lives in Core.
/// </summary>
public sealed class WindowsCapturePlatform(MessageWindow window, WindowsClipboardWriter writer)
    : ICapturePlatform
{
    private const int OpenAttempts = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);

    public uint ClipboardSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    /// <summary>
    /// 物理键盘上此刻有没有修饰键按着。只在还没发过任何模拟按键时问才准——模拟的按下与抬起也会
    /// 改动这份状态；取词与回贴都在发键之前问（<see cref="SelectionCapture"/>）。
    /// </summary>
    public bool ModifiersHeld() => ControlKeystroke.AnyModifierDown(IsDown);

    public string? ReadClipboardText()
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
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        throw new ClipboardUnavailableException("The clipboard stayed locked by another process.");
    }

    /// <summary>
    /// Null clears the clipboard rather than writing an empty string, so an
    /// empty clipboard is restored as empty.
    ///
    /// Known limitation: only text is preserved. If the user had an image on
    /// the clipboard, the target application's own copy has already replaced it
    /// by the time capture reads anything — the technique cannot save what it
    /// never saw. Worth revisiting when images arrive (issue 09).
    /// </summary>
    public bool WriteClipboardText(string? text)
        => text is null ? writer.Clear() : writer.SetText(text);

    public void SendCopyKeystroke() => SendWithControl(NativeMethods.VkC);

    public void SendPasteKeystroke() => SendWithControl(NativeMethods.VkV);

    public void Wait(TimeSpan duration) => Thread.Sleep(duration);

    /// <summary>
    /// Sends Ctrl plus one key, the strokes decided by <see cref="ControlKeystroke"/>
    /// from what is held right now. Capture runs from a hotkey and normally
    /// waits for the fingers to leave its modifiers first; when they never do,
    /// Shift/Alt/Win are let go (Ctrl+Shift+C is a different command) and a
    /// held Ctrl is borrowed rather than released — releasing it made Windows
    /// forget the Ctrl still under the user's finger, and every Ctrl shortcut
    /// after it went dead until they pressed it again (user report 2026-10-10).
    /// </summary>
    private static void SendWithControl(ushort key)
    {
        var inputs = ControlKeystroke.For(key, IsDown)
            .Select(stroke => Key(stroke.Key, stroke.Up))
            .ToArray();
        NativeMethods.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
    }

    private static bool IsDown(ushort key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private static NativeMethods.Input Key(ushort key, bool up) => new()
    {
        type = NativeMethods.InputKeyboard,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KeyboardInput
            {
                wVk = key,
                dwFlags = up ? NativeMethods.KeyEventKeyUp : 0,
            },
        },
    };
}
