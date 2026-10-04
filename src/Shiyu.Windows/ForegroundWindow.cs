using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// Remembers which window the user was in, so something that has to take focus
/// can give it back.
///
/// The quick bar's focus rule is the opposite of the badge's: it needs the
/// keyboard, so it must be activated — which means the window the user was
/// typing in has to be noted beforehand and restored before anything is
/// pasted, or the paste lands in the quick bar itself.
/// </summary>
public readonly record struct ForegroundWindow(IntPtr Handle)
{
    public static ForegroundWindow Current() => new(NativeMethods.GetForegroundWindow());

    public bool IsSomething => Handle != IntPtr.Zero;

    /// <summary>
    /// 这个窗口此刻是不是前台窗口。
    /// </summary>
    public bool IsForeground => IsSomething && NativeMethods.GetForegroundWindow() == Handle;

    /// <summary>
    /// Brings the remembered window back to the front. Windows only allows
    /// this from a process that currently owns the foreground — which, having
    /// just shown the quick bar, Shiyu does.
    ///
    /// 票 43：普通的 SetForegroundWindow 之后前台仍不是目标窗口时，才走强制序列——
    /// AllowSetForegroundWindow → AttachThreadInput → 一次 F24 → SetForegroundWindow →
    /// 逆序解挂（取自 Xtranslate，时序在 Core 的 <see cref="ForegroundReclaim"/> 里有测试）。
    /// 前台已经是目标窗口时什么都不发；绝不发 Alt：单按 Alt 会激活记事本、Office 的菜单栏，
    /// 吞掉随后的 Ctrl+V。窄条的粘贴模式顺带受益。
    /// </summary>
    /// <returns>回来之后目标窗口是否在前台——贴之前据此决定敢不敢发 Ctrl+V。</returns>
    public bool Restore() => IsSomething && ForegroundReclaim.Run(WindowsForegroundPlatform.Instance, Handle);
}
