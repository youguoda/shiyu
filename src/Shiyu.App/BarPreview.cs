using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

internal partial class BarWindow
{
    // --- preview panel ---------------------------------------------------------

    /// <summary>
    /// Executes one policy decision. Open and Retarget carry the panel to the
    /// card it belongs beside; Close takes it away. Everything time-based
    /// flows through the tick into here, so there is exactly one code path
    /// that shows and moves the panel — and one place to re-arm the timer
    /// after whatever the decision changed (O-37).
    /// </summary>
    private void RunPreviewCommand(PreviewCommand command)
    {
        // The panel inherits this window's DPI scale: before its first show it
        // has no source of its own, and a default of 1.0 misplaces it on any
        // scaled desk.
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
            ?? Matrix.Identity;

        switch (command)
        {
            case PreviewCommand.Open when CardById(_previewPolicy.Card) is { } opening:
                _connectorAnchor = AnchorFor(opening) ?? WindowRect();
                EnsurePreview().ShowFor(opening, PlacementAnchor(_connectorAnchor.Value), slide: false,
                    scaleX: scale.M11, scaleY: scale.M22, band: ZBandPolicy.FollowsHost(Topmost));
                SuppressCardTooltip(opening);
                break;

            case PreviewCommand.Retarget when CardById(_previewPolicy.Card) is { } moving:
                _connectorAnchor = AnchorFor(moving) ?? WindowRect();
                EnsurePreview().ShowFor(moving, PlacementAnchor(_connectorAnchor.Value), slide: true,
                    scaleX: scale.M11, scaleY: scale.M22, band: ZBandPolicy.FollowsHost(Topmost));
                SuppressCardTooltip(moving);
                break;

            case PreviewCommand.Close:
                _preview?.TakeDown();
                _connector?.HideCurve();
                RestoreCardTooltip();
                break;
        }

        // Whatever the event was, the set of pending deadlines may have
        // changed; the timer follows the policy's answer, arming for the next
        // one or standing down.
        ArmPreviewTick();
    }

    /// <summary>
    /// The card tooltip and the hover preview are the same channel: the tip
    /// fires first (system delay), and once the preview panel is in place it
    /// repeats the entry text in full — the two floating together only stack
    /// one over the other (user report 2026-10-03). While a preview is up the
    /// hovered card's tooltip is stashed off (clearing it closes an open tip
    /// too); closing the preview restores it, so the drag hint still teaches
    /// during short hovers. ToolTipService.IsEnabled is NOT inherited down the
    /// visual tree, so the setting has to land on the card container itself.
    /// </summary>
    private readonly List<FrameworkElement> _tipsSuppressed = [];

    private void SuppressCardTooltip(BarCard card)
    {
        RestoreCardTooltip();

        var container = PinnedList.ItemContainerGenerator.ContainerFromItem(card)
            ?? Cards.ItemContainerGenerator.ContainerFromItem(card);
        if (container is FrameworkElement element)
        {
            SuppressTips(element);
        }
    }

    /// <summary>
    /// The template hangs tips on inner elements (body text, timestamp), not on
    /// the container. Disabling beats clearing: setting ToolTip to null mid-show
    /// left an EMPTY tooltip shell parked over the cards for its whole duration
    /// (user report 2026-10-05 — the mysterious blank strip), while
    /// ToolTipService.IsEnabled=false on the same element closes the open tip
    /// immediately and blocks new ones.
    /// </summary>
    private void SuppressTips(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.ToolTip is not null)
            {
                ToolTipService.SetIsEnabled(fe, false);
                _tipsSuppressed.Add(fe);
            }

            SuppressTips(child);
        }
    }

    private void RestoreCardTooltip()
    {
        foreach (var element in _tipsSuppressed)
        {
            ToolTipService.SetIsEnabled(element, true);
        }

        _tipsSuppressed.Clear();
    }

    /// <summary>
    /// Where ShowFor anchors the panel (U-03): horizontally to the BAR's outer
    /// edge — the card used to carry the anchor and the panel pressed ~8 DIP
    /// into the bar's window — while the vertical half stays the card's, so
    /// "top-aligned with the row" survives (§3.3). The connector keeps the
    /// card rect itself: the curve points at the row, not at the window.
    /// </summary>
    private ScreenRect PlacementAnchor(ScreenRect card)
    {
        var bar = WindowRect();
        return new ScreenRect(bar.Left, card.Top, bar.Right, card.Bottom);
    }

    private ConnectorWindow? _connector;

    /// <summary>The row the panel (and its connector) currently belongs beside.</summary>
    private ScreenRect? _connectorAnchor;

    private PreviewWindow EnsurePreview()
    {
        if (_preview is null)
        {
            _preview = new PreviewWindow(_fileIcons, _fileProbe);
            _preview.PointerRestingOnPanel += () => RunPreviewCommand(_previewPolicy.PreviewEntered());
            _preview.PointerLeftPanel += () => RunPreviewCommand(_previewPolicy.PreviewLeft());
            _preview.PanelMoved += OnPreviewPanelMoved;

            // Born into the bar's z-tier (票 39): the panel never hovers above
            // windows the bar itself is under.
            _preview.Topmost = Topmost;
        }

        return _preview;
    }

    /// <summary>
    /// The connector redraws with every step the panel takes — first arrival
    /// included, which is the teleport the ticket asks for: a line flying in
    /// from the previous row's position would read as a glitch, not as craft.
    ///
    /// The curve is conditional now (§4.7, ticket 20): squarely-beside is the
    /// common case and gets the 2 DIP accent bridge on the panel's card-facing
    /// edge instead; only a real offset (clamped or squeezed) earns the curve.
    /// </summary>
    private void OnPreviewPanelMoved(ScreenRect panel)
    {
        if (_connectorAnchor is not { } anchor)
        {
            return;
        }

        var (scaleX, scaleY) = ScreenGeometry.ScaleForRect(panel);

        if (PreviewConnector.ShouldDrawCurve(anchor, panel, scaleX, scaleY))
        {
            _preview?.HideBridge();
            _connector ??= new ConnectorWindow();
            _connector.ShowCurve(
                anchor, panel, new System.Windows.Interop.WindowInteropHelper(_preview).Handle,
                ZBandPolicy.FollowsHost(Topmost));
        }
        else
        {
            _connector?.HideCurve();
            _preview?.ShowBridge();
        }
    }

    /// <summary>The panel follows the pointer only between realised cards; off-list the bar anchors it.</summary>
    private BarCard? CardById(long? id)
        => id is { } key ? VisibleRows.FirstOrDefault(card => card.Id == key) : null;

    /// <summary>
    /// The hovered or selected card's rectangle in physical pixels — where the
    /// preview hangs from. Null when the card is not realized (a keyboard move
    /// still scrolling into view); the settle beat usually resolves that.
    ///
    /// Built from the window's own physical rectangle plus the card's offset
    /// inside it, rather than PointToScreen: on a desk with mixed scale
    /// factors, PointToScreen composes through the wrong monitor's transform
    /// and the anchor lands on the wrong screen.
    /// </summary>
    private ScreenRect? AnchorFor(BarCard card)
    {
        var container = PinnedList.ItemContainerGenerator.ContainerFromItem(card);
        if (container is null)
        {
            container = Cards.ItemContainerGenerator.ContainerFromItem(card);
        }

        if (container is not FrameworkElement element || element.ActualWidth <= 0)
        {
            return null;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!WindowRects.TryGet(handle, out var window))
        {
            return null;
        }

        var offset = element.TranslatePoint(new Point(0, 0), this);
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
            ?? Matrix.Identity;

        var anchorRect = new ScreenRect(
            window.Left + (int)Math.Round(offset.X * scale.M11),
            window.Top + (int)Math.Round(offset.Y * scale.M22),
            window.Left + (int)Math.Round((offset.X + element.ActualWidth) * scale.M11),
            window.Top + (int)Math.Round((offset.Y + element.ActualHeight) * scale.M22));

        return anchorRect;
    }

    private ScreenRect WindowRect()
        => WindowRects.TryGet(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var rect)
            ? rect
            : new ScreenRect(0, 0, 0, 0);
}
