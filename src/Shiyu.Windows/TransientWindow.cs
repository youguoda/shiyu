using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// The window traits the badge and the panel both need: visible, incapable of
/// taking focus away from whatever the user is actually doing, and landed in
/// an explicit z-order band.
///
/// Exists so the application layer never reaches into the interop itself.
///
/// The band is always an argument, never a default: the summon path once
/// hard-inserted HWND_TOPMOST here, and a bar whose setting said "not
/// topmost" came back from the dead on every hotkey (票 39 验收缺陷 A/B).
/// Which surface sits in which band is ZBandPolicy's decision, in Core; this
/// file only translates a band into handles and bits.
/// </summary>
public static class TransientWindow
{
    /// <summary>
    /// Marks a window as one that is never activated — clicking it leaves the
    /// user's caret and selection exactly where they were — and lands its
    /// topmost bit to match the band.
    /// </summary>
    public static void MakeNonActivating(IntPtr handle, ZBand band)
    {
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        var withBand = band == ZBand.Topmost
            ? style | (long)NativeMethods.WsExNoActivate | (long)NativeMethods.WsExTopmost
            : (style | (long)NativeMethods.WsExNoActivate) & ~(long)NativeMethods.WsExTopmost;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(withBand));
    }

    /// <summary>
    /// Places a window at an explicit size and position, directly below
    /// another handle in the z-order, without activating it — visible first,
    /// pressed under second (O-31).
    /// </summary>
    public static void PlaceBelow(
        IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height)
        => NativeMethods.SetWindowPos(
            handle, insertAfter, x, y, width, height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);

    /// <summary>
    /// Moves a window in physical screen pixels, sidestepping the scaled
    /// coordinate system entirely — which is what makes it behave on a desk
    /// with monitors at different scale factors — and lands it in the given
    /// band at the same time, so no placement step can re-assert a bit the
    /// policy cleared.
    /// </summary>
    public static void MoveTo(IntPtr handle, ScreenPoint position, ZBand band)
        => NativeMethods.SetWindowPos(
            handle, BandHandle(band), position.X, position.Y, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);

    private static IntPtr BandHandle(ZBand band)
        => band == ZBand.Topmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNoTopmost;
}
