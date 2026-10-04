using System.Runtime.InteropServices;

namespace Shiyu.Windows;

internal delegate IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

/// <summary>
/// A low-level hook callback (WH_KEYBOARD_LL / WH_MOUSE_LL). Same shape as
/// <see cref="WindowProcedure"/>; separate name because a hook that times out
/// is silently unhooked by the system, which makes these worth their own
/// discipline at every call site.
/// </summary>
internal delegate IntPtr LowLevelHookProc(int code, IntPtr wParam, IntPtr lParam);

/// <summary>
/// The Win32 surface this application depends on. Kept in one place so the
/// platform layer reads as ordinary code and the interop stays reviewable.
/// </summary>
internal static class NativeMethods
{
    /// <summary>
    /// The hidden window is an ordinary top-level window that is simply never
    /// shown, rather than a message-only one — see MessageWindow for why.
    /// </summary>
    internal const uint WsOverlapped = 0x00000000;
    internal const uint WsExToolWindow = 0x00000080;

    internal const uint WmDestroy = 0x0002;
    internal const uint WmClipboardUpdate = 0x031D;
    internal const uint WmNull = 0x0000;

    /// <summary>
    /// "A system setting changed" — the broadcast the theme swap rides on
    /// (O-37), with the setting group's name in lParam.
    /// </summary>
    internal const uint WmSettingChange = 0x001A;

    /// <summary>
    /// The only way out of a GetMessage loop — and how the hook thread is told
    /// its services are no longer needed (LowLevelHookThread, O-16).
    /// </summary>
    internal const uint WmQuit = 0x0012;
    internal const uint WmRightButtonUp = 0x0205;
    internal const uint WmLeftButtonUp = 0x0202;

    /// <summary>Tray icons call back with an application-defined message.</summary>
    internal const uint WmTrayIcon = 0x8000 + 1;

    internal const uint CfUnicodeText = 13;

    internal const uint CfHdrop = 15;

    internal const uint NimAdd = 0x00000000;
    internal const uint NimModify = 0x00000001;
    internal const uint NimDelete = 0x00000002;
    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;
    internal const uint NifInfo = 0x00000010;

    /// <summary>Reaches every top-level window, which is why the hidden window is one.</summary>
    internal static readonly IntPtr HwndBroadcast = new(0xFFFF);

    internal const uint MfString = 0x00000000;
    internal const uint MfSeparator = 0x00000800;
    internal const uint MfGrayed = 0x00000001;

    internal const uint TpmRightButton = 0x0002;
    internal const uint TpmReturnCmd = 0x0100;

    internal static readonly IntPtr IdiApplication = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint style;
        public WindowProcedure lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    // --- 专用钩子线程的泵与停机（LowLevelHookThread，O-16）---

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    /// <summary>
    /// Returns 0 on WM_QUIT and -1 on error; a low-level hook's callbacks are
    /// delivered to the installing thread through this pump.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(
        out Msg message, IntPtr hWnd, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessageW(ref Msg message);

    /// <summary>Thread-targeted, so the hook thread can be stopped without a window.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostThreadMessageW(
        uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AddClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // --- 前台兜底（票 43，ForegroundReclaim 的 Win32 一一对应）---

    /// <summary>ASFW_ANY：允许任何进程调用 SetForegroundWindow。</summary>
    internal const uint AsfwAny = 0xFFFFFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>把一个线程的输入处理机制挂到另一个线程上（或解挂）；挂不上返回 false。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    /// <summary>
    /// GetGUIThreadInfo's answer (票 26 的插入符锚点): only the caret fields
    /// matter here. rcCaret is in the CLIENT coordinates of hwndCaret.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hWndActive;
        public IntPtr hWndFocus;
        public IntPtr hWndCapture;
        public IntPtr hWndMenuOwner;
        public IntPtr hWndMoveSize;
        public IntPtr hWndCaret;
        public Rect rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr itemId, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int TrackPopupMenuEx(
        IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessageW(string message);


    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterClipboardFormatW(string format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    /// <summary>
    /// 枚举剪贴板上现有的格式（票 43 的快照）：传 0 取第一个，之后传上一个的 id，返回 0 为止。
    /// 要求剪贴板已打开。枚举只列名字，不会让剪贴板的所有者去渲染延迟格式。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClipboardFormatNameW(
        uint format, System.Text.StringBuilder name, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr GlobalSize(IntPtr handle);


    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    internal const uint GmemMoveable = 0x0002;


    internal const uint WmHotkey = 0x0312;
    internal const uint InputKeyboard = 1;
    internal const uint KeyEventKeyUp = 0x0002;

    internal const ushort VkControl = 0x11;
    internal const ushort VkShift = 0x10;
    internal const ushort VkMenu = 0x12;
    internal const ushort VkLWin = 0x5B;
    internal const ushort VkRWin = 0x5C;
    internal const ushort VkC = 0x43;
    internal const ushort VkV = 0x56;

    /// <summary>
    /// F24：几乎没有软件响应。前台兜底发它，是为了让本进程成为"刚产生输入的进程"，
    /// 满足 SetForegroundWindow 的许可——而不像 Alt 那样会激活菜单栏（票 43）。
    /// </summary>
    internal const ushort VkF24 = 0x87;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeyboardInput ki;
        [FieldOffset(0)] public HardwareInput hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <summary>
    /// Legacy input injection, kept for the Win+V mask keystroke: keybd_event
    /// marks its output LLKHF_INJECTED exactly like SendInput, and for a
    /// down/up pair of one key it is the shorter call.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);


    internal const int GwlExStyle = -20;
    internal const uint WsExNoActivate = 0x08000000;

    /// <summary>WS_EX_TRANSPARENT: hit-testing passes through to windows below.</summary>
    internal const uint WsExTransparent = 0x00000020;
    internal const uint WsExTopmost = 0x00000008;
    internal static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>HWND_NOTOPMOST: clears the topmost bit and lands the window at the top of the normal band.</summary>
    internal static readonly IntPtr HwndNoTopmost = new(-2);
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    /// <summary>
    /// The monitor with the largest intersection with the rect (nearest on a
    /// tie or a miss) — the right anchor for a DPI question about a REGION,
    /// where the point form would answer for whichever corner was handed in.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    /// <summary>
    /// The window's rectangle in physical pixels — the one source that is
    /// right on every monitor and scale combination (O-39 收口自 App 层)。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    /// <summary>Repositions and resizes in physical pixels, repainting when asked.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool MoveWindow(
        IntPtr hWnd, int x, int y, int width, int height, bool repaint);


    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    internal const int SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(int action, uint parameter, out bool state, uint winIni);

    internal const uint ShgfiIcon = 0x00000100;
    internal const uint ShgfiLargeIcon = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // --- 低级钩子（WH_KEYBOARD_LL=13 见 WinVHook；WH_MOUSE_LL=14 见 MouseDragHook）---

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(
        int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // --- 前台窗口的类别（划词的桌面早退，票 37）---

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

}
