namespace Shiyu.Core;

/// <summary>
/// Which z-order band a Shiyu surface lands in (票 39 关置顶降级策略).
/// The Win32 layer translates a band into an insert-after handle; the choice
/// of band is decided here, where tests can pin it.
/// </summary>
public enum ZBand
{
    /// <summary>The topmost band: the WS_EX_TOPMOST bit is set, above every normal window.</summary>
    Topmost,

    /// <summary>The normal band: no topmost bit — other windows may cover it.</summary>
    Normal,
}

public static class ZBandPolicy
{
    /// <summary>
    /// The band for a surface bound to the bar — the bar itself on summon,
    /// and the pane anchored to it (the preview panel). It is the bar's own
    /// band: a covered bar must never be shadowed by its own floating pane.
    /// Surfaces NOT bound to the bar (the badge, the
    /// translation panel, the quick bar) stay in the topmost band by design —
    /// they are summoned by copies anywhere and rely on the bit to be seen.
    /// </summary>
    public static ZBand FollowsHost(bool hostPinned)
        => hostPinned ? ZBand.Topmost : ZBand.Normal;
}
