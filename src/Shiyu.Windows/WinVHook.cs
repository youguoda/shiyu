using System.Runtime.InteropServices;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// Takes Win+V over from the system clipboard panel, through a low-level
/// keyboard hook driven by the tested <see cref="WinVFilter"/>.
///
/// Two properties the takeover owes the user, both structural here:
/// the hook lives inside this process, so a crash or a kill removes it and
/// Win+V falls back to Windows on its own — nothing to clean up, nothing to
/// forget; and Dispose unhooks on the spot, so switching the setting off
/// restores the system behaviour without a restart.
///
/// Threading (O-16): the hook is installed on and serviced by a dedicated
/// pumped thread — see <see cref="LowLevelHookThread"/> for why the UI thread
/// may not own a low-level hook. The callback and the filter state machine it
/// feeds run on that hook thread and nowhere else (nothing else in this class
/// touches the filter, so there is no cross-thread state to guard); the mask
/// injection and the app trigger below stay POSTED to the context captured
/// here at construction, exactly where they ran before the migration. The
/// hook handle reaches the callback as its first argument (see
/// <see cref="LowLevelHookThread"/> for why a field read-back would race).
/// </summary>
public sealed class WinVHook : IDisposable
{
    private const int WhKeyboardLl = 13;

    // Whether Win is down is asked of Windows at every V press, not only remembered
    // from the events this hook happened to see (用户实录 2026-10-13: after Win+L the
    // release fell on the lock screen, the filter kept "Win held", and every plain V
    // summoned the bar until Shiyu quit).
    private readonly WinVFilter _filter = new(WinIsDown);
    private readonly LowLevelHookThread _host;
    private readonly SynchronizationContext? _context;

    private bool _disposed;

    public WinVHook()
    {
        _context = SynchronizationContext.Current;

        // The mask is the trick that lets the real Win release pass through
        // (so the key never sticks) without the Start menu popping: once the
        // shell has seen "some key happened while Win was down", it opens
        // nothing on the release. VK 0xFF is an unassigned no-op nothing
        // processes.
        //
        // Both the mask and the app trigger run POSTED, never inside the hook
        // callback: injecting input from within a low-level hook callback
        // re-enters the hook on the same thread — the callback stalls, the
        // system times the hook out, and the app can die outright.
        _filter.Triggered += () => _context?.Post(_ =>
        {
            NativeMethods.keybd_event(0xFF, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(0xFF, 0, 2, UIntPtr.Zero);
            Triggered?.Invoke();
        }, null);

        // Logged off the hook thread too: the callback must never wait on a file.
        _filter.StaleWinCorrected += () => _context?.Post(_ => Log.Event(LogEvent.WinVStaleWinCorrected), null);

        // Installing waits for the system's verdict: a refused hook throws
        // Win32Exception out of this constructor instead of failing invisibly
        // while the setting claims a takeover is in place (O-16).
        _host = new LowLevelHookThread(WhKeyboardLl, HookCallback, "Shiyu 键盘钩子");
    }

    /// <summary>Raised on the installing thread's context when Win+V is taken.</summary>
    public event Action? Triggered;

    private IntPtr HookCallback(IntPtr hook, int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

        // Injected keystrokes are deliberately NOT filtered: a user's own
        // automation (AutoHotkey and friends) sends Win+V the same way, and
        // Shiyu itself never synthesises this combination, so there is no
        // loop to guard against — only users to serve.
        var direction = wParam.ToInt64() switch
        {
            0x0100 or 0x0104 => KeyDirection.Down, // WM_KEYDOWN / WM_SYSKEYDOWN
            0x0101 or 0x0105 => KeyDirection.Up,   // WM_KEYUP / WM_SYSKEYUP
            _ => KeyDirection.Up,
        };

        var key = info.VkCode switch
        {
            0x5B => SpecialKey.LeftWin,
            0x5C => SpecialKey.RightWin,
            0x56 => SpecialKey.V,
            _ => SpecialKey.Other,
        };

        if (key == SpecialKey.Other && direction == KeyDirection.Up)
        {
            // Nothing the filter decides on; skip the call for speed.
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        return _filter.Feed(key, direction) == KeyFlow.Swallow
            ? new IntPtr(1)
            : NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }

    /// <summary>
    /// Either Win key down, per Windows itself (the high bit of the async state). Called on
    /// the hook thread for a V press: the Win press it answers about went through the
    /// hook chain earlier, so the system has already counted it — and a release no hook
    /// saw (the lock screen, an elevated window) is counted all the same.
    /// </summary>
    private static bool WinIsDown()
        => (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The host unhooks on the hook thread as its pump exits, then joins
        // (bounded): once this returns, the callback can no longer run and the
        // object is inert.
        _host.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
