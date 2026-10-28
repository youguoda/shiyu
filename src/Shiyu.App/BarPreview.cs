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
                SetAsideIfPinned(opening);
                EnsurePreview().ShowFor(opening, PlacementAnchor(AnchorFor(opening) ?? WindowRect()), slide: false,
                    scaleX: scale.M11, scaleY: scale.M22, band: ZBandPolicy.FollowsHost(Topmost));
                break;

            case PreviewCommand.Retarget when CardById(_previewPolicy.Card) is { } moving:
                SetAsideIfPinned(moving);
                EnsurePreview().ShowFor(moving, PlacementAnchor(AnchorFor(moving) ?? WindowRect()), slide: true,
                    scaleX: scale.M11, scaleY: scale.M22, band: ZBandPolicy.FollowsHost(Topmost));
                break;

            case PreviewCommand.Close:
                _preview?.TakeDown();
                break;
        }

        // Whatever the event was, the set of pending deadlines may have
        // changed; the timer follows the policy's answer, arming for the next
        // one or standing down.
        ArmPreviewTick();
    }

    /// <summary>
    /// Where ShowFor anchors the panel (U-03): horizontally to the BAR's outer
    /// edge — the card used to carry the anchor and the panel pressed ~8 DIP
    /// into the bar's window — while the vertical half stays the card's, so
    /// "top-aligned with the row" survives (§3.3). That alignment is the whole
    /// tie between panel and row now: the connector line and its bridge were
    /// removed at the user's request (2026-10-05).
    /// </summary>
    private ScreenRect PlacementAnchor(ScreenRect card)
    {
        var bar = WindowRect();
        return new ScreenRect(bar.Left, card.Top, bar.Right, card.Bottom);
    }

    // 卡片悬停教学已迁至预览面板尾行（2026-10-05）：系统 ToolTip 的关闭
    // 行为两次留下空壳（用户实录），收起/恢复它的那套逻辑随之整体退役。

    private PreviewWindow EnsurePreview()
    {
        if (_preview is null)
        {
            _preview = new PreviewWindow(_fileIcons, _fileProbe);
            _preview.PointerRestingOnPanel += OnPreviewPointerResting;
            _preview.PointerLeftPanel += OnPreviewPointerLeft;
            _preview.WorkbenchAction += RunWorkbenchAction;

            // Born into the bar's z-tier (票 39): the panel never hovers above
            // windows the bar itself is under.
            _preview.Topmost = Topmost;
        }

        return _preview;
    }

    private void OnPreviewPointerResting() => RunPreviewCommand(_previewPolicy.PreviewEntered());

    private void OnPreviewPointerLeft() => RunPreviewCommand(_previewPolicy.PreviewLeft());

    // --- 同时钉住多扇（用户需求 2026-10-28）--------------------------------------------------
    //
    // 钉住的那扇先照常挂在窄条上；要看另一张卡时才把它挪到一旁、另开一扇——只钉一扇时什么都不变。
    // 挪到一旁的不再随悬停开收，取消钉住（它自己收起）就关掉；最多留 8 扇，再多先关最早钉的。

    private readonly List<PreviewWindow> _pinnedPreviews = [];

    private const int PinnedPreviewsKept = 8;

    private void SetAsideIfPinned(BarCard card)
    {
        if (_preview is not { IsPinned: true } pinned || pinned.CardId == card.Id)
        {
            return;
        }

        pinned.PointerRestingOnPanel -= OnPreviewPointerResting;
        pinned.PointerLeftPanel -= OnPreviewPointerLeft;
        pinned.IsVisibleChanged += OnSetAsideVisibility;
        _pinnedPreviews.Add(pinned);
        _preview = null;

        while (_pinnedPreviews.Count > PinnedPreviewsKept)
        {
            var oldest = _pinnedPreviews[0];
            _pinnedPreviews.RemoveAt(0);
            oldest.IsVisibleChanged -= OnSetAsideVisibility;
            oldest.TakeDown(force: true);
            oldest.Close();
        }
    }

    /// <summary>挪到一旁的那扇取消钉住后自己收起：收完就关，不留着空窗。</summary>
    private void OnSetAsideVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue || sender is not PreviewWindow window)
        {
            return;
        }

        window.IsVisibleChanged -= OnSetAsideVisibility;
        _pinnedPreviews.Remove(window);
        window.Close();
    }

    /// <summary>
    /// Ctrl+T（用户需求 2026-10-28）：开着一扇没钉的就钉它（看到什么钉什么）；开着的正钉着选中的卡就取消；
    /// 别的情况把选中的卡片直接钉成一扇（原来钉着的另一扇挪到一旁）。
    /// </summary>
    private void PinPreviewFromKey()
    {
        if (_preview is { IsVisible: true } shown && (!shown.IsPinned || shown.CardId == _selected?.Id))
        {
            shown.TogglePin();
            return;
        }

        if (_selected is not { } card)
        {
            return;
        }

        SetAsideIfPinned(card);
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var preview = EnsurePreview();
        preview.ShowFor(card, PlacementAnchor(AnchorFor(card) ?? WindowRect()), slide: false,
            scaleX: scale.M11, scaleY: scale.M22, band: ZBandPolicy.FollowsHost(Topmost));
        if (!preview.IsPinned)
        {
            preview.TogglePin();
        }
    }

    /// <summary>这张卡被删了：钉着它的每一扇都强制收起（钉着一张已删条目是幽灵）。</summary>
    private void TakeDownPinnedPreviewsOf(long id)
    {
        foreach (var window in _pinnedPreviews.Where(window => window.CardId == id).ToList())
        {
            window.TakeDown(force: true);
        }
    }

    /// <summary>宿主关窗：挪到一旁的钉住预览一并关掉，不比窄条活得久。</summary>
    private void ClosePinnedPreviews()
    {
        foreach (var window in _pinnedPreviews.ToList())
        {
            window.IsVisibleChanged -= OnSetAsideVisibility;
            window.TakeDown(force: true);
            window.Close();
        }

        _pinnedPreviews.Clear();
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
