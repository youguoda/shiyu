using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// The full-content preview panel (ticket 17): hold Space or rest the pointer
/// on a card, and the whole entry appears beside the bar — text scrollable,
/// images at true proportion, files with every path.
///
/// The panel opens at its final size. That size is computed before anything is
/// shown, from numbers the database already has: wrapped line count for text
/// (measured with the real font, which is arithmetic, not layout), pixel size
/// for images, row count for files. A window that grew after appearing would
/// read as a guess.
///
/// Like the badge it never activates, so reading it costs the user nothing.
/// Keys therefore arrive at the bar that owns it, not here.
/// </summary>
internal partial class PreviewWindow : Window
{
    private readonly FileTypeIcons _fileIcons;

    /// <summary>
    /// The existence verdicts shared with the bar's cards (O-36): reading a
    /// verdict is a dictionary lookup, never a disk round-trip — an offline
    /// network original used to freeze this panel on the spot.
    /// </summary>
    private readonly FileExistenceCache _fileProbe;

    /// <summary>
    /// Drops every decode and probe this panel still has in flight. Invalidated
    /// each time the panel moves to another card: a late original must never
    /// overwrite the next card's picture, and late verdicts must not strike
    /// through the wrong rows.
    /// </summary>
    private readonly BackfillGate _backfills = new();

    /// <summary>The file-row views, for restyling when the probes answer.</summary>
    private readonly List<(string Path, TextBlock Name)> _fileRows = [];

    /// <summary>The entry the panel is showing right now.</summary>
    public long CardId { get; private set; }

    /// <summary>The card filled into the panel; the very same card again leaves the content alone.</summary>
    private BarCard? _shown;

    /// <summary>Raised when the pointer comes to rest on the panel — the bar suspends its close buffer.</summary>
    public event Action? PointerRestingOnPanel;

    public event Action? PointerLeftPanel;

    public PreviewWindow(FileTypeIcons fileIcons, FileExistenceCache fileProbe)
    {
        InitializeComponent();
        _fileIcons = fileIcons;
        _fileProbe = fileProbe;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);

        // The band is whatever ShowFor last decided (验收缺陷 B): the birth
        // style once OR-ed the topmost bit in unconditionally, so a preview
        // born while the bar was unpinned floated above windows the bar was
        // under. _band defaults to Topmost — ShowFor sets it before the first
        // handle exists, so this reads the right value at birth.
        TransientWindow.MakeNonActivating(helper.Handle, _band);

        // The shell contract (§4.7, ticket 20): DWM paints the contour, the
        // XAML paints surface only. A system that refuses the attributes gets
        // the self-drawn stroke and shadow instead.
        if (!DwmEffects.TryApplyPanel(helper.Handle))
        {
            Backdrop.DegradeShell(Root);
        }

        // WS_EX_NOACTIVATE alone is not enough: a click that lands on
        // focusable content inside (a scrollable body, say) makes WPF raise
        // the window to foreground anyway. Answering WM_MOUSEACTIVATE with
        // MA_NOACTIVATE closes that path at the source — reading the panel
        // never costs the user their caret.
        if (HwndSource.FromHwnd(helper.Handle) is { } source)
        {
            source.AddHook(RefuseActivation);
        }

        // 钉住工作台的指针交互（滚轮缩放、拖拽平移）。
        MouseWheel += OnWorkbenchWheel;
        MouseLeftButtonDown += OnWorkbenchPress;
        MouseMove += OnWorkbenchMove;
        MouseLeftButtonUp += OnWorkbenchRelease;
    }

    private const int WmMouseActivate = 0x21;

    private const int MaNoActivate = 3;

    private IntPtr RefuseActivation(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!Pinned && msg == WmMouseActivate)
        {
            handled = true;
            return (IntPtr)MaNoActivate;
        }

        return IntPtr.Zero;
    }

    // --- showing ----------------------------------------------------------------

    /// <summary>
    /// The z band this panel currently lives in. The panel is bound to the
    /// bar: it opens beside the bar's cards and follows the bar's own band
    /// (票 39 关置顶降级). ShowFor refreshes it before every landing.
    /// </summary>
    private ZBand _band = ZBand.Topmost;

    /// <summary>
    /// Shows (or moves) the panel for a card, anchored beside a card rectangle
    /// in physical pixels. The size is decided first, the position second, and
    /// the content last — the shape never changes once on screen.
    ///
    /// The DPI scale comes from the caller: before this window's first show it
    /// has no presentation source of its own to read one from, and a guessed
    /// 1.0 would place a 1.5x panel as if it were a third narrower.
    /// </summary>
    public void ShowFor(BarCard card, ScreenRect anchor, bool slide, double scaleX, double scaleY, ZBand band)
    {
        // Band first, before anything can create the handle: birth style,
        // WPF's own Topmost, and every SetWindowPos below all read it, so the
        // panel never lands one step outside the bar's tier.
        _band = band;
        Topmost = band == ZBand.Topmost;

        // Called back mid fade-out (Space released and pressed again within
        // the fade): the panel stays, so the fade must not finish by hiding it.
        if (_takingDown)
        {
            _takingDown = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }

        // 钉着的预览独占：别的卡片不再夺走它（取消钉后才恢复悬停规则）。
        if (Pinned && !ReferenceEquals(card, _shown))
        {
            return;
        }

        // 钉着的预览独占：别的卡片不再夺走它（取消钉后才恢复悬停规则）。
        if (Pinned && !ReferenceEquals(card, _shown))
        {
            return;
        }

        // 未钉的常态：每次显示都把「永不激活」摆回去（钉住的一轮摘过它，
        // 同一句柄会跨显示复用——预览不能带着可激活的样子复活）。
        if (!Pinned)
        {
            TransientWindow.MakeNonActivating(new System.Windows.Interop.WindowInteropHelper(this).Handle, _band);
        }

        var (width, height) = Measure(card, scaleX);
        Width = width;
        Height = height;

        var wasVisible = IsVisible;

        // The same card again (a held key's repeat, the pointer coming back
        // from the panel): its content stays as it is. Filling it anew put the
        // thumbnail back over an original that had already landed and dropped
        // the decode in flight — at a key repeat's pace the picture flickered
        // between blurry and sharp (用户实录 2026-10-09).
        if (!wasVisible || !ReferenceEquals(card, _shown))
        {
            // Everything this panel had in flight describes the previous card;
            // its results are dropped the moment the panel moves on (O-36).
            _backfills.Invalidate();
            Fill(card, scaleX);
            _shown = card;
            CardId = card.Id;
        }

        var placed = PreviewPlacement.Place(
            anchor,
            (int)Math.Ceiling(width * scaleX),
            (int)Math.Ceiling(height * scaleY),
            ScreenGeometry.WorkAreaAt(new ScreenPoint(
                (anchor.Left + anchor.Right) / 2,
                (anchor.Top + anchor.Bottom) / 2)),
            gap: (int)Math.Round(PreviewPlacement.Gap * scaleX));

        if (!wasVisible)
        {
            // The spike's rule: a layered window appears at full opacity — a
            // fade-in from transparent never composites. First placement is a
            // jump, not a slide: there is nothing to slide from.
            Opacity = 1;
            BeginAnimation(OpacityProperty, null);
            var helper = new WindowInteropHelper(this);
            _ = helper.EnsureHandle();
            TransientWindow.MoveTo(helper.Handle, placed, _band);
            Show();
            return;
        }

        if (slide && UiAnimation.Allowed())
        {
            // A retarget glides: the same panel carried across reads as one
            // continuous thing, where a close-and-reopen reads as flicker.
            // The glide is driven in physical pixels on purpose — WPF's
            // Left/Top know nothing of a window positioned by SetWindowPos
            // and would snap it back to a stale value mid-animation.
            SlideTo(placed);
        }
        else
        {
            var handle = new WindowInteropHelper(this).Handle;
            TransientWindow.MoveTo(handle, placed, _band);
        }
    }

    private System.Windows.Threading.DispatcherTimer? _slide;

    /// <summary>
    /// Lerps the window to its new spot with SetWindowPos over the standard
    /// fast duration — reduced motion lands here as one instant step, the
    /// same deal every other motion in the app gets.
    /// </summary>
    private void SlideTo(ScreenPoint target)
    {
        _slide?.Stop();

        var handle = new WindowInteropHelper(this).Handle;
        if (Shiyu.Windows.WindowRects.TryGet(handle, out var current))
        {
            var start = new ScreenPoint(current.Left, current.Top);
            var duration = MotionPlan.Duration(animationsAllowed: true);
            var clock = Stopwatch.StartNew();

            void Step(ScreenPoint at) => TransientWindow.MoveTo(handle, at, _band);

            _slide = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };

            _slide.Tick += (_, _) =>
            {
                var elapsed = clock.Elapsed;
                if (elapsed >= duration)
                {
                    _slide.Stop();
                    Step(target);
                    return;
                }

                var progress = duration.Ticks == 0 ? 1.0 : (double)elapsed.Ticks / duration.Ticks;
                var eased = 1 - Math.Pow(1 - progress, 3);

                Step(new ScreenPoint(
                    (int)Math.Round(start.X + (target.X - start.X) * eased),
                    (int)Math.Round(start.Y + (target.Y - start.Y) * eased)));
            };

            _slide.Start();
        }
        else
        {
            TransientWindow.MoveTo(handle, target, _band);
        }
    }

    /// <summary>A fade-out is running; <see cref="ShowFor"/> calling the panel back cancels it.</summary>
    private bool _takingDown;

    /// <summary>Removes the panel. The fade starts from a painted surface, so it composites.
    /// 钉住（用户需求 2026-10-19）挡住一切自动收起；结构性的收起（宿主关闭、钉着的卡片被删）
    /// 带 force 照常执行——预览不能比宿主活得久，也不能钉着一张已删条目当幽灵。</summary>
    public void TakeDown(bool force = false)
    {
        if (!IsVisible)
        {
            return;
        }

        if (Pinned && !force)
        {
            // 钉着：任何自动收起都无效。
            return;
        }

        Pinned = false;

        _takingDown = true;
        var fade = Motion.Fade(0);
        fade.Completed += (_, _) =>
        {
            if (!_takingDown)
            {
                // expected: 淡出途中被叫回来了——面板留着，这次淡出作废。
                return;
            }

            _takingDown = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // --- 钉住（用户需求 2026-10-19）：钉着就不随悬停收起，一直在屏幕最上 ---------

    /// <summary>钉着时不走任何收起，也不被其他卡片夺走；只显示预览正文（元信息行与教学行收起）。</summary>
    private bool Pinned { get; set; }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        if (Pinned)
        {
            Unpin();
        }
        else
        {
            Pin();
        }
    }

    private void Pin()
    {
        Pinned = true;

        // 会话式窗口化：摘掉「永不激活」，点击与打字才进得来（用户点钉就是授权）。
        TransientWindow.MakeActivatable(new System.Windows.Interop.WindowInteropHelper(this).Handle, _band);
        UpdatePinVisual();
        SetupWorkbench();
    }

    /// <summary>取消钉：预览的使命到此为止——立即收起，悬停规则恢复。</summary>
    private void Unpin()
    {
        Pinned = false;
        TransientWindow.MakeNonActivating(new System.Windows.Interop.WindowInteropHelper(this).Handle, _band);
        UpdatePinVisual();
        TakeDown(force: true);
    }

    private void UpdatePinVisual()
    {
        PinGlyphFull.Visibility = Pinned ? Visibility.Visible : Visibility.Collapsed;
        PinGlyphEmpty.Visibility = Pinned ? Visibility.Collapsed : Visibility.Visible;
        var state = Pinned ? "已钉住：保持显示 · 点一下取消" : "钉住，保持显示";
        PinButton.ToolTip = state;
        System.Windows.Automation.AutomationProperties.SetName(PinButton, state);

        // 干净模式：钉着只留预览正文，元信息行与教学行都收。
        WhenText.Visibility = Pinned ? Visibility.Collapsed : Visibility.Visible;
        KindText.Visibility = Pinned ? Visibility.Collapsed : Visibility.Visible;
        TeachHost.Visibility = Pinned ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 工作台动作（用户需求 2026-10-20）：动作行的芯片点下去，带着此刻的
    /// 文本与条目交给宿主（窄条）执行——预览自己不持有剪贴板与库。
    /// </summary>
    internal event Action<string, long, string>? WorkbenchAction;

    /// <summary>动作行的一枚芯片：显示名 + 动作名。</summary>
    private sealed record WorkbenchChip(string Label, string Action);

    internal void ProbeClickPin() =>
        PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal bool ProbePinned => Pinned;

    internal bool ProbeMetaVisible => WhenText.IsVisible || KindText.IsVisible;

    internal bool ProbeTeachVisible => TeachHost.IsVisible;

    // --- 钉住工作台（用户需求 2026-10-20）：文字可编、图片可缩放，动作按形态出 ---------

    private double _imageScale = 1;
    private Point _imageOrigin = new(0, 0);
    private bool _imageDragging;
    private Point _dragStart;

    /// <summary>
    /// 钉住那一刻把窗变成工作台：文本类正文换编辑框、颜色给色块、动作行按
    /// 形态出芯片；图片初始化缩放变换。取消钉（TeardownWorkbench）全部还原。
    /// </summary>
    private void SetupWorkbench()
    {
        var card = _shown;
        if (card is null)
        {
            return;
        }

        var isText = card.Kind != EntryKind.Image && card.Kind != EntryKind.Files;
        SubtypeColor colour = default;
        var isColour = card.Subtype == EntrySubtype.Color
            && SubtypeColor.TryParse(card.Text, out colour);
        if (isColour)
        {
            SwatchHost.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(colour.A, colour.R, colour.G, colour.B));
        }
        SwatchHost.Visibility = isColour ? Visibility.Visible : Visibility.Collapsed;

        EditHost.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        TextHost.Visibility = isText ? Visibility.Collapsed : TextHost.Visibility;
        EditHost.Text = card.Text;

        ActionRow.Items.Clear();

        // 芯片点击走附加事件：模板里的按钮点下去，带着自己的动作名。
        ActionRow.AddHandler(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is System.Windows.Controls.Button { DataContext: WorkbenchChip chip }
                    && _shown is { } card)
                {
                    WorkbenchAction?.Invoke(chip.Action, card.Id, WorkbenchText());
                }
            }));

        foreach (var chip in WorkbenchChipsFor(card))
        {
            ActionRow.Items.Add(chip);
        }

        ActionRow.Visibility = Visibility.Visible;
        TeachHost.Visibility = Visibility.Collapsed;
        WhenText.Visibility = Visibility.Collapsed;
        KindText.Visibility = Visibility.Collapsed;
    }

    private void TeardownWorkbench()
    {
        ActionRow.Items.Clear();
        ActionRow.Visibility = Visibility.Collapsed;
        EditHost.Visibility = Visibility.Collapsed;
        SwatchHost.Visibility = Visibility.Collapsed;
        _imageScale = 1;
        _imageOrigin = new Point(0, 0);
        ApplyImageTransform();
    }

    /// <summary>按形态给动作芯片（评审方案 2026-10-20 的每形态表）。</summary>
    private static IReadOnlyList<WorkbenchChip> WorkbenchChipsFor(BarCard card)
    {
        return card.Kind switch
        {
            EntryKind.Files =>
            [
                new WorkbenchChip("打开", "open-path"),
                new WorkbenchChip("定位", "locate-path"),
                new WorkbenchChip("复制路径", "copy"),
            ],
            EntryKind.Image =>
            [
                new WorkbenchChip("复制图片", "copy-image"),
            ],
            _ => card.Subtype switch
            {
                EntrySubtype.Link =>
                [
                    new WorkbenchChip("打开", "open-uri"),
                    new WorkbenchChip("复制", "copy"),
                    new WorkbenchChip("存为新条目", "save-as-new"),
                ],
                EntrySubtype.Email =>
                [
                    new WorkbenchChip("写邮件", "open-uri"),
                    new WorkbenchChip("复制", "copy"),
                    new WorkbenchChip("存为新条目", "save-as-new"),
                ],
                EntrySubtype.LocalPath =>
                [
                    new WorkbenchChip("打开", "open-path"),
                    new WorkbenchChip("定位", "locate-path"),
                    new WorkbenchChip("复制", "copy"),
                ],
                EntrySubtype.Color =>
                [
                    new WorkbenchChip("复制色值", "copy"),
                ],
                _ =>
                [
                    new WorkbenchChip("复制", "copy"),
                    new WorkbenchChip("存为新条目", "save-as-new"),
                ],
            },
        };
    }

    /// <summary>动作行此刻的文本：编辑框里改过的算数，没编辑就是原文。</summary>
    private string WorkbenchText()
        => EditHost.Visibility == Visibility.Visible ? EditHost.Text : (_shown?.Text ?? string.Empty);

    private void ApplyImageTransform()
    {
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(_imageScale, _imageScale));
        group.Children.Add(new TranslateTransform(_imageOrigin.X, _imageOrigin.Y));
        ImageHost.RenderTransform = group;
    }

    private void OnWorkbenchWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Pinned || _shown is not { Kind: EntryKind.Image })
        {
            return;
        }

        var position = e.GetPosition(ImageHost);
        var newScale = Math.Clamp(_imageScale * (e.Delta > 0 ? 1.25 : 0.8), 1.0, 8.0);

        // 朝光标缩放：光标下的图像点，缩放后仍落在光标下。
        _imageOrigin = new Point(
            position.X - (position.X - _imageOrigin.X) * newScale / _imageScale,
            position.Y - (position.Y - _imageOrigin.Y) * newScale / _imageScale);
        _imageScale = newScale;

        if (_imageScale <= 1.001)
        {
            _imageScale = 1;
            _imageOrigin = new Point(0, 0);
        }

        ApplyImageTransform();
        e.Handled = true;
    }

    private void OnWorkbenchPress(object sender, MouseButtonEventArgs e)
    {
        if (!Pinned || _shown is not { Kind: EntryKind.Image } || !ImageHost.IsVisible)
        {
            return;
        }

        // 放大之后才有得拖：原始比例下拖动没有意义，点击穿到下面去。
        if (_imageScale > 1.001)
        {
            _imageDragging = true;
            _dragStart = e.GetPosition(ImageHost);
            ImageHost.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnWorkbenchMove(object sender, MouseEventArgs e)
    {
        if (!_imageDragging)
        {
            return;
        }

        var position = e.GetPosition(ImageHost);
        _imageOrigin = new Point(
            _imageOrigin.X + position.X - _dragStart.X,
            _imageOrigin.Y + position.Y - _dragStart.Y);
        ApplyImageTransform();
        e.Handled = true;
    }

    private void OnWorkbenchRelease(object sender, MouseButtonEventArgs e)
    {
        if (_imageDragging)
        {
            _imageDragging = false;
            ImageHost.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void OnWorkbenchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!Pinned || _shown is not { Kind: EntryKind.Image } || !ImageHost.IsVisible)
        {
            return;
        }

        _imageScale = 1;
        _imageOrigin = new Point(0, 0);
        ApplyImageTransform();
        e.Handled = true;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => PointerRestingOnPanel?.Invoke();

    private void OnMouseLeave(object sender, MouseEventArgs e) => PointerLeftPanel?.Invoke();

    // --- sizing -------------------------------------------------------------------

    /// <summary>
    /// The panel's final size, from <see cref="PreviewSizing"/> with the
    /// caller-measured text numbers. Images hand in their stored pixels; files
    /// their row count. The teach row rides under every kind: it adds its one
    /// line of fixed height, and a panel narrower than the row widens to fit
    /// it (never past MaxWidth) — so the row neither wraps nor clips, and the
    /// estimate can never come out short again (the 2026-10-05 clipped row).
    /// Images also take the screen's scale: one image pixel to one screen pixel at most.
    /// </summary>
    private (double Width, double Height) Measure(BarCard card, double deviceScale)
    {
        var lineHeight = ThemeManager.Content.ContentLine;

        var size = card.Kind switch
        {
            EntryKind.Image => PreviewSizing.ForImage(card.PixelWidth, card.PixelHeight, deviceScale),
            EntryKind.Files => PreviewSizing.ForFiles(card.Files.Count, FileRowHeight),
            _ => TextSize(card, lineHeight),
        };

        if (ShowsTeaching(card))
        {
            var width = Math.Min(
                PreviewSizing.MaxWidth,
                Math.Max(size.Width, TeachRowWidth(card.Teaching) + PreviewSizing.ChromeHorizontal));
            size = (width, size.Height + TeachRowHeight);
        }

        return size;
    }

    private static bool ShowsTeaching(BarCard card) => card.ShowToolTip && card.Teaching.Count > 0;

    /// <summary>
    /// The teach row's height: 10 margin above the divider + 1 divider + 8
    /// padding + one 18 DIP key-cap line. A constant, because the row never
    /// wraps — <see cref="Measure"/> widens the panel instead.
    /// </summary>
    private const double TeachRowHeight = 10 + 1 + 8 + 18;

    /// <summary>
    /// The row's natural width, worked out like the text is: per step a cap
    /// (its label plus 5 + 5 padding, at least 18 wide), 5 to the action, the
    /// action, 14 to the next step. The arithmetic measures the regular face;
    /// the caps are SemiBold, a hair wider — 2 DIP per cap covers it.
    /// </summary>
    private static double TeachRowWidth(IReadOnlyList<TeachStep> steps)
    {
        var width = 0.0;
        foreach (var step in steps)
        {
            var cap = Formatted(step.Key, DesignTokens.TypeKeyCap, constrain: PreviewSizing.MaxWidth).Width;
            var action = Formatted(step.Action, DesignTokens.TypeCaption, constrain: PreviewSizing.MaxWidth).Width;
            width += Math.Max(18, cap + 10 + 2) + 5 + action + 14;
        }

        return width;
    }

    private (double Width, double Height) TextSize(BarCard card, double lineHeight)
    {
        var box = PreviewSizing.MaxWidth - PreviewSizing.ChromeHorizontal;
        var formatted = Formatted(
            card.Text,
            ThemeManager.Content.Content,
            constrain: box);
        var lineCount = (int)Math.Ceiling(formatted.Height / lineHeight);
        var textWidth = Math.Min(formatted.Width, box);

        return PreviewSizing.ForText(lineCount, textWidth, lineHeight);
    }

    /// <summary>One file row: a line and its breathing room, in the mono-content size of the 内容字号 in force.</summary>
    private static double FileRowHeight => ThemeManager.Content.MonoLine;

    /// <summary>
    /// Measures wrapped text with the real font. This is the "worked out, not
    /// laid out" half of the size rule: no control is created, nothing is
    /// shown, the arithmetic just runs.
    /// </summary>
    private static FormattedText Formatted(string text, double size, double constrain)
    {
        var typeface = new Typeface(DesignTokens.FamilyUi);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            Brushes.Transparent,
            pixelsPerDip: 1.0)
        {
            MaxTextWidth = constrain,
        };

        return formatted;
    }

    // --- content -------------------------------------------------------------------

    private void Fill(BarCard card, double deviceScale)
    {
        // The card's label already carries an image's pixel size ("图片 ·
        // 800×500"); appending it here again printed it twice. An image whose
        // original is gone (cleared by the retention period, say) can only
        // show its 240-pixel thumbnail: the header says so, rather than
        // leaving the blur unexplained.
        KindText.Text = card.Kind == EntryKind.Image && !card.HasOriginal
            ? card.KindText + " · 原图已不在，这是缩略图"
            : card.KindText;
        WhenText.Text = card.WhenText;

        TextHost.Visibility = card.Kind == EntryKind.Text ? Visibility.Visible : Visibility.Collapsed;
        ImageHost.Visibility = card.Kind == EntryKind.Image ? Visibility.Visible : Visibility.Collapsed;
        FilesHost.Visibility = card.Kind == EntryKind.Files ? Visibility.Visible : Visibility.Collapsed;

        TextBody.Text = card.Text;
        FilesBody.Children.Clear();
        _fileRows.Clear();

        // 教学行（2026-10-05 迁入，同日改为键帽 + 短动作）：气泡退役后的唯一
        // 教学位，由 bar.card-tooltips 决定显隐——设置关掉就是一点提示都不剩。
        TeachRow.ItemsSource = card.Teaching;
        // 钉着只留正文：教学行不回来（同卡刷新路径会重算这行）。
        TeachHost.Visibility = ShowsTeaching(card) && !Pinned ? Visibility.Visible : Visibility.Collapsed;

        if (card.Kind == EntryKind.Image)
        {
            // One box for both pictures: the thumbnail and the original fill
            // exactly the same rectangle, so the upgrade never shifts a pixel.
            // The box is the image at one image pixel per screen pixel at most.
            var display = PreviewSizing.ImageDisplay(card.PixelWidth, card.PixelHeight, deviceScale);
            ImageHost.MaxWidth = display?.Width ?? double.PositiveInfinity;
            ImageHost.MaxHeight = display?.Height ?? double.PositiveInfinity;

            // Decoded to exactly the screen pixels the box covers, both sides —
            // one bitmap pixel per screen pixel, edges flush with the box. A row
            // without a stored size decodes by width alone (height 0: keep shape).
            var decode = display is { } box
                ? PreviewSizing.DecodeSize(card.PixelWidth, card.PixelHeight, box, deviceScale)
                : (PreviewSizing.DecodeWidth(0, Width - PreviewSizing.ChromeHorizontal, deviceScale), 0);

            if (card.OriginalPath is { Length: > 0 } path && KeptOriginal(path, decode) is { } kept)
            {
                // Seen moments ago (Space let go and pressed again, a step
                // back up the list): sharp at once, no thumbnail in between.
                ImageHost.Source = kept;
            }
            else
            {
                // The thumbnail first — it is already decoded, and it is the
                // database's forever promise — with the original upgrading it
                // from a background decode when it lands (O-36).
                ImageHost.Source = card.Thumbnail;
                LoadOriginalBehind(card, decode);
            }
        }

        if (card.Kind == EntryKind.Files)
        {
            // Every path, not the card's clamp: "which file was that" is
            // answered by the list, and a path that no longer exists says so
            // here the same way the card does there. Verdicts come from the
            // cache; the probes for paths nobody has a fresh verdict on run
            // behind the paint and restyle the rows they answer for.
            foreach (var path in card.Files)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };

                var icon = new Image
                {
                    Source = _fileIcons.For(path, _fileProbe.Lookup(path)?.IsDirectory ?? false),
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Focusable = false,
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                row.Children.Add(icon);

                var name = new TextBlock
                {
                    Text = path,
                    FontSize = ThemeManager.Content.Mono,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DressRow(name, dead: !(_fileProbe.Lookup(path)?.Exists ?? true));

                row.Children.Add(name);
                FilesBody.Children.Add(row);
                _fileRows.Add((path, name));
            }

            ProbeFileRows(card);
        }
    }

    private static void DressRow(TextBlock name, bool dead)
    {
        name.SetResourceReference(TextBlock.ForegroundProperty,
            dead ? "Brush.TextTertiary" : "Brush.Text");
        name.TextDecorations = dead ? System.Windows.TextDecorations.Strikethrough : null;
        name.ToolTip = dead ? "路径不存在" : null;
    }

    /// <summary>
    /// Probes the paths this panel shows and no one has a fresh verdict for,
    /// then restyles the rows with the answers. On the thread pool: an
    /// offline network path costs its SMB timeout there, not on the panel.
    /// </summary>
    private void ProbeFileRows(BarCard card)
    {
        var stale = card.Files.Where(_fileProbe.WantsProbe).ToArray();
        if (stale.Length == 0)
        {
            return;
        }

        var generation = _backfills.Epoch;
        var dispatcher = Dispatcher;

        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var path in stale)
            {
                var file = File.Exists(path);
                var directory = !file && Directory.Exists(path);
                _fileProbe.Record(path, file || directory, directory);
            }

            dispatcher.BeginInvoke(() =>
            {
                if (!_backfills.IsCurrent(generation))
                {
                    return;
                }

                foreach (var (path, name) in _fileRows)
                {
                    DressRow(name, dead: !(_fileProbe.Lookup(path)?.Exists ?? true));
                }
            });
        });
    }

    /// <summary>
    /// Originals decoded moments ago, newest first, by path and decode size.
    /// A few only — each is up to a few megabytes — enough for Space let go
    /// and pressed again, or a step back up the list, to be sharp at once.
    /// </summary>
    private readonly List<(string Path, (int Width, int Height) Size, BitmapSource Image)> _originals = [];

    private const int OriginalsKept = 4;

    private BitmapSource? KeptOriginal(string path, (int Width, int Height) size)
    {
        var index = _originals.FindIndex(kept => kept.Size == size && kept.Path == path);
        if (index < 0)
        {
            return null;
        }

        var kept = _originals[index];
        _originals.RemoveAt(index);
        _originals.Insert(0, kept);
        return kept.Image;
    }

    private void KeepOriginal(string path, (int Width, int Height) size, BitmapSource image)
    {
        _originals.RemoveAll(kept => kept.Size == size && kept.Path == path);
        _originals.Insert(0, (path, size, image));
        if (_originals.Count > OriginalsKept)
        {
            _originals.RemoveRange(OriginalsKept, _originals.Count - OriginalsKept);
        }
    }

    /// <summary>
    /// The original at full fidelity, decoded at the screen pixels the panel
    /// shows it with, on the thread pool (O-36) — the file read is disk
    /// latency, an offline network original is an SMB timeout, and neither
    /// belongs on the thread that draws. The width used to be the box's DIP
    /// width: on a 150% screen that is two thirds of the pixels shown, and
    /// the preview came out soft (用户实录 2026-10-09). The upgrade lands only
    /// if the panel still shows the card it was decoded for; the thumbnail it
    /// would replace is the database's forever promise either way.
    /// </summary>
    private void LoadOriginalBehind(BarCard card, (int Width, int Height) decode)
    {
        if (card.OriginalPath is not { Length: > 0 } path)
        {
            return;
        }

        var wanted = card.Id;
        var generation = _backfills.Epoch;
        var dispatcher = Dispatcher;

        System.Threading.Tasks.Task.Run(() =>
        {
            var file = File.Exists(path);
            _fileProbe.Record(path, file, isDirectory: false);

            if (!file)
            {
                return;
            }

            BitmapSource? original = null;
            try
            {
                original = Decode(new Uri(path), decode);
            }
            catch (Exception failure) when (
                failure is IOException or UnauthorizedAccessException
                or NotSupportedException or System.IO.FileFormatException)
            {
                // expected: 原图损坏或截断——缩略图继续当值，它是数据库
                // 永远守住的承诺。
            }

            if (original is null)
            {
                return;
            }

            dispatcher.BeginInvoke(() =>
            {
                // Kept even when the panel has moved on: coming back to this
                // card is exactly when it pays.
                KeepOriginal(path, decode, original);

                if (_backfills.IsCurrent(generation) && CardId == wanted)
                {
                    ImageHost.Source = original;
                }
            });
        });
    }

    /// <summary>Decodes at a pixel size; a height of 0 keeps the picture's shape from the width.</summary>
    private static BitmapSource Decode(Uri source, (int Width, int Height) size)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = size.Width;
        if (size.Height > 0)
        {
            image.DecodePixelHeight = size.Height;
        }

        image.UriSource = source;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
