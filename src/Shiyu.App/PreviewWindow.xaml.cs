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
    }

    private const int WmMouseActivate = 0x21;

    private const int MaNoActivate = 3;

    private static IntPtr RefuseActivation(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate)
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

        var (width, height) = Measure(card);
        Width = width;
        Height = height;

        // Everything this panel had in flight describes the previous card;
        // its results are dropped the moment the panel moves on (O-36).
        _backfills.Invalidate();

        var wasVisible = IsVisible;
        Fill(card);
        CardId = card.Id;

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

    /// <summary>Removes the panel. The fade starts from a painted surface, so it composites.</summary>
    public void TakeDown()
    {
        if (!IsVisible)
        {
            return;
        }

        var fade = Motion.Fade(0);
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
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
    /// </summary>
    private (double Width, double Height) Measure(BarCard card)
    {
        var lineHeight = ThemeManager.Content.ContentLine;

        var size = card.Kind switch
        {
            EntryKind.Image => PreviewSizing.ForImage(card.PixelWidth, card.PixelHeight),
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

    private void Fill(BarCard card)
    {
        // The card's label already carries an image's pixel size ("图片 ·
        // 800×500"); appending it here again printed it twice.
        KindText.Text = card.KindText;
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
        TeachHost.Visibility = ShowsTeaching(card) ? Visibility.Visible : Visibility.Collapsed;

        if (card.Kind == EntryKind.Image)
        {
            // The thumbnail first — it is already decoded, and it is the
            // database's forever promise — with the original upgrading it
            // from a background decode when it lands (O-36).
            ImageHost.Source = card.Thumbnail;
            LoadOriginalBehind(card);
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
    /// The original at full fidelity, decoded no larger than the panel needs,
    /// on the thread pool (O-36) — the file read is disk latency, an offline
    /// network original is an SMB timeout, and neither belongs on the thread
    /// that draws. The upgrade lands only if the panel still shows the card
    /// it was decoded for; the thumbnail it would replace is the database's
    /// forever promise either way.
    /// </summary>
    private void LoadOriginalBehind(BarCard card)
    {
        if (card.OriginalPath is not { Length: > 0 } path)
        {
            return;
        }

        var boxWidth = Math.Max(1, (int)(Width - PreviewSizing.ChromeHorizontal));
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
                original = Decode(new Uri(path), boxWidth);
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
                if (_backfills.IsCurrent(generation) && CardId == wanted)
                {
                    ImageHost.Source = original;
                }
            });
        });
    }

    private static BitmapSource Decode(Uri source, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth;
        image.UriSource = source;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
