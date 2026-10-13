using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shiyu.App;

/// <summary>
/// A card's text body, typed so the window can find it in a realized row —
/// among other things to dress it as a link while Ctrl is held.
/// </summary>
internal sealed class EntryBodyText : TextBlock
{
}

/// <summary>
/// The card face（票 21 / UI 报告 §6.1 卡片）：a Border that also knows when the
/// press that will become its "pressed" state is down. The four-state overlay
/// and the selection ring are siblings laid over it, so no state ever costs
/// layout — but a plain Border has no pressed notion to bind to, and the
/// template needs one (hover alone comes free on UIElement).
///
/// Presses swallowed by a tray button (marked handled before they bubble)
/// never reach these class handlers, so an action press never paints the card
/// underneath.
/// </summary>
internal sealed class CardSurface : Border
{
    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.Register(
        nameof(IsPressed), typeof(bool), typeof(CardSurface), new FrameworkPropertyMetadata(false));

    /// <summary>Whether the left button that presses this card is down. Drives the 9% state layer.</summary>
    public bool IsPressed
    {
        get => (bool)GetValue(IsPressedProperty);
        private set => SetValue(IsPressedProperty, value);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        IsPressed = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        IsPressed = false;
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        // The release can happen off-card (no capture is taken here); leaving
        // is the reliable end of any press this surface still believes in.
        IsPressed = false;
        base.OnMouseLeave(e);
    }
}

/// <summary>
/// The timestamp that yields its place to the hover actions. Fades and slides
/// left when the tray opens, returns when it closes — the two read as one
/// handover rather than as two separate events.
/// </summary>
internal sealed class HandoverText : TextBlock
{
    public HandoverText()
    {
        RenderTransform = new TranslateTransform();
    }

    public void Yield()
    {
        BeginAnimation(OpacityProperty, Motion.Fade(0));

        if (RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, Motion.Double(-8));
        }
    }

    public void Return()
    {
        BeginAnimation(OpacityProperty, Motion.Fade(1));

        if (RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, Motion.Double(0));
        }
    }

    /// <summary>Snaps back to the resting state, recycling-safe.</summary>
    public void Reset()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        if (RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = 0;
        }
    }
}

/// <summary>
/// The key badge that covers a row's source-app icon while Ctrl is held,
/// showing the number key that pastes that row. Opaque, same eighteen units,
/// exactly over the icon: one element to toggle, no layout movement.
/// </summary>
internal sealed class RowKeyBadge : Border
{
    public RowKeyBadge()
    {
        SetResourceReference(CornerRadiusProperty, "Radius.Thumb");
        SetResourceReference(BackgroundProperty, "Brush.Accent");

        var label = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 键帽数字走 Type.KeyCap（12，票 19 字阶 v2 的键帽专用档）。
        label.SetResourceReference(TextBlock.FontSizeProperty, "Type.KeyCap");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextOnAccent");
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("RowKeyText"));
        Child = label;
    }
}

/// <summary>
/// The row container. Exists to reconfigure and reset the hover tray at
/// exactly the moment recycling hands this container to another row — the
/// issue 02 spike's finding about animation state following containers.
/// </summary>
internal sealed class BarCardContainer : ListBoxItem
{
    private BarCard? _subscribed;

    public BarCardContainer()
    {
        // Recycling in a nutshell: the same container instance gets a new
        // row's DataContext.
        DataContextChanged += (_, _) => PrepareTray();

        // First realization has a hole the DataContext event cannot cover: it
        // fires before the template applies, when the tray does not exist in
        // the tree yet. Loaded fires once the visual tree is in place, which
        // is the first moment the tray can actually be configured. Without
        // this retry, every freshly realized row ends up with an empty tray.
        Loaded += (_, _) => PrepareTray();
    }

    public void PrepareTray()
    {
        var tray = Tree.FindDescendant<ActionTray>(this);
        var handover = Tree.FindDescendant<HandoverText>(this);
        var badge = Tree.FindDescendant<RowKeyBadge>(this);

        if (DataContext is not BarCard card || tray is null)
        {
            return;
        }

        if (!ReferenceEquals(card, _subscribed))
        {
            if (_subscribed is not null)
            {
                _subscribed.PropertyChanged -= OnCardChanged;
            }

            _subscribed = card;
            card.PropertyChanged += OnCardChanged;
        }

        if (Window.GetWindow(this) is BarWindow host)
        {
            tray.Configure(card, host.ActionsFor(card));

            // Resubscribed rather than accumulated: the tray instance
            // survives recycling and would otherwise fire twice per press.
            tray.ActionExecuted -= host.RunHoverAction;
            tray.ActionExecuted += host.RunHoverAction;
            tray.MoreRequested -= host.OpenTrayMore;
            tray.MoreRequested += host.OpenTrayMore;

            // A row realized while Ctrl is held must arrive with its badge.
            host.ApplyKeyHintsTo(badge, tray);

            // 上屏的图片卡换上按屏幕像素解码的原图（用户实录 2026-10-14：图片模糊）。
            host.RequestSharpImage(card);
        }

        tray.Reset();
        handover?.Reset();

        // Reconfigured while the pointer never left (favouriting flips delete
        // protection): the tray reopens rather than snapping shut under the
        // cursor waiting for an enter that will not come.
        if (IsMouseOver)
        {
            tray.Open();
        }
    }

    /// <summary>
    /// Favouriting toggles delete protection, and the tray's actions are
    /// chosen when the row was built — without this, a card starred a second
    /// ago keeps showing the delete button the promise says is gone.
    /// </summary>
    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BarCard.Favorite))
        {
            PrepareTray();
        }
    }
}

internal sealed class BarCardsList : ListBox
{
    protected override DependencyObject GetContainerForItemOverride()
        => new BarCardContainer();

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);

        if (element is BarCardContainer card)
        {
            card.PrepareTray();
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is BarCardContainer card)
        {
            card.PrepareTray();
        }

        base.ClearContainerForItemOverride(element, item);
    }
}

internal static class Tree
{
    internal static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

internal static class BrushExtensions
{
    /// <summary>A brush frozen for sharing across recycled rows.</summary>
    public static Brush FrozenBrush(this SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
