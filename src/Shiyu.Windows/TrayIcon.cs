using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Shiyu.Windows;

/// <summary>托盘菜单的一行数据：<see cref="Label"/> 为 null 是分隔线；Key 是回传给 <see cref="Command"/> 的稳定标识。</summary>
/// <param name="Accelerator">右对齐加速键列的文本（Win32 菜单惯例，\t 分列）；null 或空 = 本行不带键。</param>
public sealed record TrayMenuRow(string Key, string? Label, string? Accelerator);

/// <summary>
/// The tray icon and its menu, via <c>Shell_NotifyIcon</c> and a Win32 popup
/// menu. Deliberately not WinForms' NotifyIcon: loading the whole of Windows
/// Forms into the process for one tray icon works against the memory budget
/// this application exists to respect.
///
/// The menu is data the app supplies（§5.2 重排，票 25）：动作 + 加速键列，
/// 不再罗列灰显的最近剪贴板内容——共享屏幕时那是一份意外的泄露清单。
/// KeyMap 在 App 层把标签与组合键渲染成行，这里只管画与回传。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// Explorer broadcasts this when it restarts, having forgotten every tray
    /// icon. Without re-adding ours here, the icon would vanish for good and
    /// the application would still be running with no way to reach it.
    /// </summary>
    private static readonly uint TaskbarCreated =
        NativeMethods.RegisterWindowMessageW("TaskbarCreated");

    /// <summary>
    /// Where the .NET host puts the exe's ApplicationIcon: resource id 32512 in
    /// the exe itself. It is the same number as IDI_APPLICATION, which is how
    /// the tray long showed Windows' generic blank icon while looking right in
    /// code — LoadIconW(NULL, IDI_APPLICATION) asks the system, not the exe.
    /// </summary>
    private static readonly IntPtr AppIconResource = new(32512);

    private readonly MessageWindow _window;
    private NativeMethods.NotifyIconData _data;
    private bool _added;
    private bool _disposed;

    /// <summary>The icon this instance loaded and must destroy; zero when it fell back to the shared system icon.</summary>
    private IntPtr _ownedIcon;

    /// <summary>
    /// Supplies the whole menu: action rows with their accelerator column and
    /// separators (Label null), top to bottom, in the shape the UI report
    /// pins (§5.2). Rebuilt on every open — a hotkey changed in settings
    /// shows up the very next right click.
    /// </summary>
    public Func<IReadOnlyList<TrayMenuRow>>? Menu { get; set; }

    /// <summary>Raised with the clicked row's Key — the one event every menu action comes through.</summary>
    public event Action<string>? Command;

    /// <summary>Raised on a left click. Where it leads is the owner's call; the icon only reports it.</summary>
    public event Action? Clicked;

    public TrayIcon(MessageWindow window, string tooltip)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
        _ownedIcon = LoadAppIcon();

        _data = new NativeMethods.NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            hWnd = window.Handle,
            uID = 1,
            uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
            uCallbackMessage = NativeMethods.WmTrayIcon,
            hIcon = _ownedIcon != IntPtr.Zero
                ? _ownedIcon
                : NativeMethods.LoadIconW(IntPtr.Zero, NativeMethods.IdiApplication),
            szTip = Truncate(tooltip, 127),
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NimAdd, ref _data);
        if (!_added)
        {
            throw new InvalidOperationException(
                "Could not add the tray icon.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private void OnMessage(WindowMessage message)
    {
        if (message.Id == TaskbarCreated && !_disposed)
        {
            _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NimAdd, ref _data);
            return;
        }

        if (message.Id != NativeMethods.WmTrayIcon)
        {
            return;
        }

        var trigger = (uint)(message.LParam.ToInt64() & 0xFFFF);
        switch (trigger)
        {
            // Left click is the icon's one direct action; the menu stays one
            // right click away.
            case NativeMethods.WmLeftButtonUp:
                message.Handle();
                Clicked?.Invoke();
                break;

            case NativeMethods.WmRightButtonUp:
                message.Handle();
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Command ids are indexes plus one: TrackPopupMenuEx answers the
            // id, the rows list answers what the id meant.
            var rows = Menu?.Invoke() ?? [];
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                if (row.Label is not { Length: > 0 })
                {
                    NativeMethods.AppendMenuW(menu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
                    continue;
                }

                var text = row.Accelerator is { Length: > 0 } accelerator
                    ? $"{row.Label}\t{accelerator}"
                    : row.Label;
                NativeMethods.AppendMenuW(
                    menu, NativeMethods.MfString, new UIntPtr((uint)(index + 1)), Escape(text));
            }

            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                return;
            }

            // Windows will not dismiss a tray menu on click-away unless the
            // owning window was brought to the foreground first, and needs a
            // message afterwards to finish tearing the menu down.
            NativeMethods.SetForegroundWindow(_window.Handle);

            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturnCmd,
                cursor.X, cursor.Y, _window.Handle, IntPtr.Zero);

            NativeMethods.PostMessageW(_window.Handle, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command > 0 && command <= rows.Count)
            {
                Command?.Invoke(rows[(int)command - 1].Key);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Shows a balloon from the tray icon. Shiyu has no window to put a message
    /// in, so this is the only way it can say anything to the user.
    /// </summary>
    public void ShowNotification(string title, string message)
    {
        if (!_added)
        {
            return;
        }

        // A copy, so NIF_INFO does not stay set on the stored data and make
        // every later update pop a balloon of its own.
        var notification = _data;
        notification.uFlags = NativeMethods.NifInfo;
        notification.szInfoTitle = Truncate(title, 63);
        notification.szInfo = Truncate(message, 255);

        NativeMethods.Shell_NotifyIconW(NativeMethods.NimModify, ref notification);
    }

    /// <summary>Ampersands would otherwise be read as keyboard accelerators.</summary>
    private static string Escape(string text) => text.Replace("&", "&&");

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.MessageReceived -= OnMessage;

        if (_added)
        {
            NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref _data);
            _added = false;
        }

        if (_ownedIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_ownedIcon);
            _ownedIcon = IntPtr.Zero;
        }
    }

    /// <summary>
    /// The app's own icon at the tray's exact size for this DPI (16/20/24/32 px at
    /// 100/125/150/200%), so the shell shows the small-size master instead of
    /// shrinking the 32 px one into mush. Zero when the process carries no such
    /// resource (a bare host, tests): the caller falls back to the system icon.
    /// </summary>
    internal static IntPtr LoadAppIcon()
    {
        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxSmIcon, NativeMethods.GetDpiForSystem());
        return NativeMethods.LoadImageW(
            NativeMethods.GetModuleHandleW(null), AppIconResource, NativeMethods.ImageIcon, size, size, 0);
    }
}
