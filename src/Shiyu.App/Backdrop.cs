using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>Which system material a window asks DWM to paint behind it.</summary>
internal enum BackdropKind
{
    /// <summary>No material — only rounding and the dark-mode flag apply.</summary>
    None,

    /// <summary>Wallpaper-sampled, for long-lived windows.</summary>
    Mica,

    /// <summary>Real-time blur, for transient surfaces.</summary>
    Acrylic,
}

/// <summary>
/// Applies Windows 11's system backdrops through DWM, per ticket 03's spike:
/// the material renders only on layered windows (a normal WPF window's opaque
/// redirect surface paints black right over it), so callers attach kinds to
/// the layered surfaces and the rest get rounding plus the dark-mode flag.
///
/// Every DWM call is best-effort — but no longer silently so (ticket 20):
/// <see cref="Attach(Window, Func{BackdropKind}, Action{bool})"/> reports
/// whether the system actually took the shell attributes, because the overlay
/// family's XAML now draws nothing DWM was supposed to paint. A window that
/// hears "no" falls back to drawing its own 1 DIP stroke and shadow inside a
/// 12 DIP transparent margin (<see cref="DegradeShell"/>); on an old system
/// that is exactly ticket 03's degradation path, and it costs the product
/// zero version branches.
/// </summary>
internal static class Backdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int BACKDROP_MICA = 2;
    private const int BACKDROP_ACRYLIC = 3;

    /// <summary>
    /// The transparent rim a shell keeps when it must paint its own shadow
    /// (§4.7): <see cref="System.Windows.Media.Effects.DropShadowEffect"/>
    /// bleeds outside the element, and an edge-to-edge shell would clip it.
    /// </summary>
    public const double FallbackMargin = 12;

    private static readonly List<(Window Window, Func<BackdropKind> Kind, Action<bool>? Applied)> Attached = [];

    /// <summary>Whether the theme currently on is Dark — set by the ThemeManager.</summary>
    public static bool DarkTheme { get; set; }

    /// <summary>
    /// Attaches a material to a window for its whole lifetime and re-applies it
    /// whenever the theme moves. Safe to call from the constructor: the DWM
    /// work waits for the handle to exist.
    /// </summary>
    public static void Attach(Window window, Func<BackdropKind> kind)
        => Attach(window, kind, applied: null);

    /// <summary>
    /// <see cref="Attach(Window, Func{BackdropKind})"/> plus the verdict:
    /// <paramref name="applied"/> fires with true once DWM has accepted the
    /// shell attributes (rounding, and the material when one was asked for),
    /// or with false when this system will not paint them and the window is
    /// on its own — the overlay shells' cue to draw their fallback chrome.
    /// </summary>
    public static void Attach(Window window, Func<BackdropKind> kind, Action<bool>? applied)
    {
        Attached.Add((window, kind, applied));
        window.Closed += (_, _) => Attached.RemoveAll(a => a.Window == window || !a.Window.IsLoaded);
        window.SourceInitialized += (_, _) => applied?.Invoke(Apply(window, kind(), DarkTheme));

        if (window.IsLoaded)
        {
            applied?.Invoke(Apply(window, kind(), DarkTheme));
        }
    }

    /// <summary>
    /// The overlay family's shell contract (§4.7, ticket 20): the XAML paints
    /// surface and nothing else — no border, no shadow — because DWM owns the
    /// rounding and the drop shadow. This attach wires the verdict to that
    /// contract: a system that refuses the attributes gets the self-drawn
    /// chrome instead (<see cref="DegradeShell"/>).
    /// </summary>
    public static void AttachShell(Window window, Border shell, Func<BackdropKind> kind)
        => Attach(window, kind, systemPainted =>
        {
            if (!systemPainted)
            {
                DegradeShell(shell);
            }
        });

    /// <summary>
    /// The fallback a shell draws when DWM will not paint it: an opaque
    /// surface fill (Win10 的关键一档——壳的材质底 <c>Brush.SurfaceMaterial</c> 是刻意半透明的，
    /// 背后没有 DWM 材质时透出桌面，Windows 10 实录 2026-10-17), a 1 DIP CardStroke outline,
    /// the shared floating shadow, and the 12 DIP transparent margin the shadow needs to
    /// exist at all. Idempotent and token-driven, so a theme swap re-resolves the colours
    /// on its own.
    /// </summary>
    public static void DegradeShell(Border shell)
    {
        shell.Margin = new Thickness(FallbackMargin);
        shell.BorderThickness = new Thickness(1);
        shell.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        shell.SetResourceReference(Border.BorderBrushProperty, "Brush.CardStroke");
        shell.SetResourceReference(Border.EffectProperty, "Shadow.Floating");
    }

    /// <summary>Called by the ThemeManager after every palette swap.</summary>
    public static void SyncToTheme(bool dark)
    {
        DarkTheme = dark;

        for (var index = Attached.Count - 1; index >= 0; index--)
        {
            var (window, kind, applied) = Attached[index];
            if (!window.IsLoaded)
            {
                Attached.RemoveAt(index);
                continue;
            }

            applied?.Invoke(Apply(window, kind(), dark));
        }
    }

    /// <summary>
    /// Whether the system took the shell attributes. The corner preference is
    /// the load-bearing half: with it refused there is no system-drawn contour
    /// at all, and a refused material leaves the shell's own translucent fill
    /// exposed — both are the self-drawn-chrome case.
    /// </summary>
    private static bool Apply(Window window, BackdropKind kind, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        _ = TrySet(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
        var rounded = TrySet(handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        var material = kind == BackdropKind.None
            || TrySet(handle, DWMWA_SYSTEMBACKDROP_TYPE, kind == BackdropKind.Mica ? BACKDROP_MICA : BACKDROP_ACRYLIC);

        return rounded && material;
    }

    private static bool TrySet(IntPtr handle, int attribute, int value)
        => DwmEffects.TrySetWindowAttribute(handle, attribute, value);
}
