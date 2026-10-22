#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 窄条的调试探针区（只在 Debug 构建存在，同 App.DebugProbes.cs）。
/// </summary>
internal partial class BarWindow
{
    /// <summary>
    /// 探针命令 bar-newest：走真实路径复现"打开窄条停在最底部、看到的是最旧的
    /// 记录"（用户实录 2026-10-04）。每条路先把三页长的列表翻到底，再做一个
    /// 动作——收起重开（轻量开 / 关）、条开着人在别处时来一条外部写、条开着
    /// 再按一次快速粘贴、人在条里时来一条外部写——逐条把滚动状态记进数据目录
    /// 的 bar-newest.log，探针脚本据此判定。外部写从线程池发出，与剪贴板流水线
    /// 同一条跨线程的路。窄条由拥有者先钉住（BarModule.ProbeStartsAtNewest）：
    /// "人在别处、条还开着"只有钉住的窄条才有。
    /// </summary>
    internal async void ProbeStartsAtNewest()
    {
        var log = new StringBuilder();

        ScrollViewer? Viewer() => Tree.FindDescendant<ScrollViewer>(Cards);

        // ContextIdle runs after layout, and a page fetched by the scroll
        // handler needs another pass of its own: three rounds settle both.
        static async Task Settle()
        {
            for (var round = 0; round < 3; round++)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
        }

        async Task<double> ScrollToBottom()
        {
            // Each round lands at the end and pages the next page in.
            for (var round = 0; round < 4; round++)
            {
                Viewer()?.ScrollToEnd();
                await Settle();
            }

            return Viewer()?.VerticalOffset ?? -1;
        }

        void Note(string path, double before) => log.AppendLine(FormattableString.Invariant(
            $"{path} before={before:F0} offset={Viewer()?.VerticalOffset ?? -1:F0} loaded={_browser.Loaded.Count} total={_store.Count()} active={IsActive}"));

        async Task WriteFromElsewhere(string text)
        {
            await Task.Run(() => _store.Append(text, "probe-elsewhere", DateTimeOffset.Now));
            await Settle();
        }

        // Older than everything the seed wrote: the newest entries stay the
        // seed's, and the list becomes three pages long.
        var now = DateTimeOffset.Now;
        _store.AppendMany(Enumerable.Range(0, 250).Select(i =>
            new NewEntry($"probe filler {i:D3}", "probe-filler", now.AddDays(-3).AddMinutes(-i))));

        SummonForPaste();
        await Settle();

        // 1. Hide and summon again, lightweight teardown on (the default).
        var before = await ScrollToBottom();
        Dismiss();
        await Settle();
        SummonForPaste();
        await Settle();
        Note("reopen", before);

        // 2. The same with the teardown off: cards and offset survive the hide.
        _refreshPolicy.ApplySettings(lightweightWhenHidden: false);
        before = await ScrollToBottom();
        Dismiss();
        await Settle();
        SummonForPaste();
        await Settle();
        Note("reopen-kept-cards", before);
        _refreshPolicy.ApplySettings(_settings.LightweightWhenHidden);

        // 3. Open, unused: another window holds the focus when the copy lands.
        before = await ScrollToBottom();
        var elsewhere = new Window
        {
            Title = "probe-elsewhere",
            Width = 160,
            Height = 90,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left,
            Top = Top,
        };
        elsewhere.Show();
        elsewhere.Activate();
        await Settle();
        await WriteFromElsewhere("probe copy while away");
        Note("copy-while-away", before);
        elsewhere.Close();

        // 4. The quick-paste key again, over the open pinned bar.
        before = await ScrollToBottom();
        SummonForPaste();
        await Settle();
        Note("paste-summon", before);
        Dismiss();
        await Settle();

        // 5. In use: the bar holds the focus — the place must stay put. The
        // log carries whether the focus was really ours; the script skips
        // the check when the foreground lock said no.
        SummonForPaste();
        Activate();
        await Settle();
        before = await ScrollToBottom();
        await WriteFromElsewhere("probe copy while in use");
        Note("copy-while-in-use", before);

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(DebugOverrides.ProbeDirectory!, "bar-newest.log"), log.ToString());
    }

    /// <summary>
    /// 探针命令 preview-image（用户实录 2026-10-09：预览图片会抖动、是低分辨率的）。窄条开着，
    /// 选中第一张图片卡；脚本从外面按下空格并按住——真实的 WM_KEYDOWN，连着自动重复——这里
    /// 每几毫秒看一眼预览面板上的图：换了几次、有没有从原图退回缩略图、最后解码了多少像素、
    /// 画了多大。日志一行一事。
    /// </summary>
    internal async void ProbePreviewImage()
    {
        var log = new StringBuilder();
        var directory = DebugOverrides.ProbeDirectory!;

        SummonForPaste();
        for (var round = 0; round < 3; round++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        // The thumbnails backfill from the thread pool; wait for the card's.
        BarCard? card = null;
        for (var waited = 0; waited < 50 && card?.Thumbnail is null; waited++)
        {
            card = VisibleRows.FirstOrDefault(row => row.Kind == EntryKind.Image);
            await Task.Delay(100);
        }

        if (card is null)
        {
            File.WriteAllText(Path.Combine(directory, "preview-image.log"), "image|missing" + Environment.NewLine + "done" + Environment.NewLine);
            return;
        }

        Select(card);
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        log.AppendLine(FormattableString.Invariant(
            $"selected|pixels={card.PixelWidth}x{card.PixelHeight}|original={card.HasOriginal}|scale={scale:F2}"));

        // The script presses Space only once the image card is the selection.
        File.WriteAllText(Path.Combine(directory, "preview-image.ready"), "ready");

        // Watch the panel's picture from now until well after the script lets go of Space.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        System.Windows.Media.ImageSource? last = null;
        var changes = 0;
        var backToThumbnail = 0;
        var sawOriginal = false;
        while (clock.Elapsed < TimeSpan.FromSeconds(9))
        {
            var source = _preview is { IsVisible: true } shown ? shown.ImageHost.Source : null;
            if (source is not null && !ReferenceEquals(source, last))
            {
                changes++;
                var thumbnail = ReferenceEquals(source, card.Thumbnail);
                if (thumbnail && sawOriginal)
                {
                    backToThumbnail++;
                }

                sawOriginal |= !thumbnail;
                log.AppendLine(FormattableString.Invariant(
                    $"source|at={clock.ElapsedMilliseconds}|kind={(thumbnail ? "thumbnail" : "original")}|px={(source as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth ?? -1}"));
                last = source;
            }

            await Task.Delay(5);
        }

        var display = PreviewSizing.ImageDisplay(card.PixelWidth, card.PixelHeight, scale, _preview?.ImageBounds);
        var expected = display is { } box ? PreviewSizing.DecodeSize(card.PixelWidth, card.PixelHeight, box, scale) : (0, 0);
        var final = last as System.Windows.Media.Imaging.BitmapSource;
        log.AppendLine(FormattableString.Invariant(
            $"final|kind={(last is null ? "none" : ReferenceEquals(last, card.Thumbnail) ? "thumbnail" : "original")}|px={final?.PixelWidth ?? -1}x{final?.PixelHeight ?? -1}|expected={expected.Item1}x{expected.Item2}|shown={_preview?.ImageHost.ActualWidth ?? -1:F2}x{_preview?.ImageHost.ActualHeight ?? -1:F2}|display={display?.Width ?? -1:F2}x{display?.Height ?? -1:F2}|changes={changes}|backToThumbnail={backToThumbnail}"));

        if (_preview?.Content is FrameworkElement panel)
        {
            App.RenderToPng(panel, Path.Combine(directory, "preview-image.png"));
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "preview-image.log"), log.ToString());
    }

    // --- 探针 bar-pin 的抓手（用户需求 2026-10-10，常驻钉住；编排在 BarModule） ---

    internal FrameworkElement ProbeHeader => Header;

    /// <summary>图钉此刻的字形码位：E718 空心（没钉住）、E841 实心（钉住）。</summary>
    internal int ProbePinGlyph => PinGlyph.Text.Length > 0 ? PinGlyph.Text[0] : 0;

    internal bool ProbePinAccented => ReferenceEquals(PinGlyph.Foreground, TryFindResource("Brush.Accent"));

    internal IntPtr ProbeHandle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    internal IntPtr ProbeReturnTarget => _returnTo.Handle;

    internal string ProbeQuery
    {
        get => SearchBox.Text;
        set => SearchBox.Text = value;
    }

    /// <summary>
    /// 探针命令 bar-sharp（用户实录 2026-10-14：窄条中的图片显示模糊）。把每张图片卡依次滚上屏，
    /// 等它的原图解码落定，记：原图尺寸、原图在不在、换没换上清晰的那张、实际解码出的像素宽、按
    /// <see cref="CardImageFit"/> 应有的宽、图片区在屏幕上占的物理像素宽。被拿掉原图的那张
    /// （<paramref name="expiredId"/>）该留在缩略图上，按缩略图本身的尺寸解码。渲一张 PNG。
    /// </summary>
    internal async void ProbeSharpImages(long expiredId)
    {
        var log = new StringBuilder();

        static async Task Settle(int milliseconds)
        {
            await Task.Delay(milliseconds);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        await Settle(1000);
        var (boxWidth, scale) = ImageBox();
        foreach (var card in _cards.Where(card => card.Kind == EntryKind.Image).ToList())
        {
            Cards.ScrollIntoView(card);
            await Settle(600);

            var decoded = (card.Thumbnail as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth ?? 0;
            var expected = CardImageFit.DecodeWidth(card.PixelWidth, card.PixelHeight, boxWidth, card.ImageHeight, scale);
            var body = Cards.ItemContainerGenerator.ContainerFromItem(card) is DependencyObject container
                ? Named(container, "ImageBody")
                : null;
            var shown = body is null ? -1 : (int)Math.Ceiling(body.ActualWidth * scale);
            log.AppendLine(FormattableString.Invariant(
                $"image|expired={card.Id == expiredId}|original={card.PixelWidth}x{card.PixelHeight}|hasOriginal={card.HasOriginal}|sharp={card.IsSharp}|decoded={decoded}|expected={expected}|shown={shown}|scale={scale:F2}"));
        }

        // 渲图停在一张换上了原图的卡上：留在缩略图上的那张看不出差别。
        if ((_cards.FirstOrDefault(card => card.IsSharp) ?? _cards.FirstOrDefault(card => card.Kind == EntryKind.Image)) is { } pictured)
        {
            Cards.ScrollIntoView(pictured);
            await Settle(300);
        }

        var directory = DebugOverrides.ProbeDirectory!;
        if (Content is FrameworkElement root)
        {
            App.RenderToPng(root, Path.Combine(directory, "bar-sharp.png"));
        }

        File.WriteAllText(Path.Combine(directory, "bar-sharp.log"), log + "done" + Environment.NewLine);
    }

    /// <summary>
    /// 探针命令 source-icons 的窄条一段（用户需求 2026-10-14：来源图标可以关，空着时什么都不放）。
    /// 每张已实现的卡一行：卡上有没有图标、图标格在不在、格里显出几张图（拾语标志的回退已撤，
    /// 取不到图标时应为 0）。
    /// </summary>
    internal string ProbeIconSlots(string phase)
    {
        var log = new StringBuilder();
        foreach (var container in RealizedContainers())
        {
            if (container.DataContext is not BarCard card || Named(container, "IconSlot") is not { } slot)
            {
                continue;
            }

            var images = ProbeVisuals(slot).OfType<System.Windows.Controls.Image>().Count(image => image.IsVisible);
            log.AppendLine(FormattableString.Invariant(
                $"bar-{phase}|icon={card.Icon is not null}|slot={slot.IsVisible}|images={images}"));
        }

        if (Content is FrameworkElement root)
        {
            App.RenderToPng(root, Path.Combine(DebugOverrides.ProbeDirectory!, $"source-icons-bar-{phase}.png"));
        }

        return log.ToString();
    }

    /// <summary>
    /// 图标格收起时按住 Ctrl：数字键帽仍浮在这一行最左端（不占布局，什么都不挪），18 见方。
    /// 量第一张卡：键帽是否显出、宽度、离这一行左缘多远、写的是哪个数字。
    /// </summary>
    internal async Task<string> ProbeKeycap()
    {
        SetKeyHints(true);
        for (var round = 0; round < 3; round++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        var first = RealizedContainers().FirstOrDefault();
        var badge = first is null ? null : Tree.FindDescendant<RowKeyBadge>(first);
        var row = first is null ? null : Named(first, "MetaRow");
        var line = badge is null || row is null
            ? "keycap|missing"
            : FormattableString.Invariant(
                $"keycap|visible={badge.IsVisible}|width={badge.ActualWidth:F0}|left={badge.TranslatePoint(new Point(0, 0), row).X:F0}|text={(badge.Child as TextBlock)?.Text}");
        SetKeyHints(false);
        return line + Environment.NewLine;
    }

    private static FrameworkElement? Named(DependencyObject root, string name)
        => ProbeVisuals(root).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == name);

    private static IEnumerable<DependencyObject> ProbeVisuals(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in ProbeVisuals(child))
            {
                yield return deeper;
            }
        }
    }

    internal void ProbeClickPin()
        => PinToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal void ProbeClickSettings()
        => SettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>头部第一行从左到右：搜索框右缘、图钉、齿轮的左缘（DIP，相对窗口）。</summary>
    internal string ProbeHeaderLayout()
    {
        double LeftOf(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), this).X;
        return FormattableString.Invariant(
            $"search-right={LeftOf(SearchBox) + SearchBox.ActualWidth:F0}|pin={LeftOf(PinToggle):F0}|settings={LeftOf(SettingsButton):F0}|settings-right={LeftOf(SettingsButton) + SettingsButton.ActualWidth:F0}|width={ActualWidth:F0}|pin-visible={PinToggle.IsVisible}|settings-visible={SettingsButton.IsVisible}");
    }

    /// <summary>
    /// 像用户点下没激活的窄条那样，先送一条 WM_MOUSEACTIVATE——走的是窗口真装上的那个钩子。
    /// </summary>
    internal void ProbeMouseActivate()
    {
        const int HtClient = 1;
        const int WmLButtonDown = 0x0201;
        var handle = ProbeHandle;
        _ = SendMessage(handle, WmMouseActivate, handle, (IntPtr)((WmLButtonDown << 16) | HtClient));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 探针命令 image-paste 的管理窗一段（用户实录 2026-10-19：窄条里图片卡按 Enter，贴出去的是
    /// 「图片 宽×高」标签而不是图片。复现用；会动真剪贴板，不进 run-all）：选中这条图片卡、
    /// 走 PasteEntry，读回剪贴板——文本标签在、图片不在，即复现。判断在应用里做完，日志只有真假与标签。
    /// </summary>
    internal async Task<string> ProbeImagePaste(long entryId, string expectedLabel)
    {
        var log = new StringBuilder();
        var card = _cards.FirstOrDefault(item => item.Id == entryId);
        if (card is null)
        {
            ReloadData();
            await Task.Delay(400);
            card = _cards.FirstOrDefault(item => item.Id == entryId);
        }

        if (card is null)
        {
            return "repro|card=False\n";
        }

        PasteEntry(card);
        await Task.Delay(800);

        var hasImage = System.Windows.Clipboard.ContainsImage();
        var text = System.Windows.Clipboard.GetText();
        var line =
            $"repro|kind={card.Kind}|containsImage={hasImage}"
            + $"|label={Flat(text) == Flat(expectedLabel)}"
            + $"|text={Flat(text)}";
        log.AppendLine(line);
        return log.ToString();

        static string Flat(string value)
            => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// 探针命令 folder-open-locate 的管理窗一段（用户实录 2026-10-19：复制的是文件夹时，「打开」与
    /// 「定位」没有反应。复现用）：按 OpenOriginal/LocateOriginal 的解析原样走一遍——
    /// <c>card.Files.FirstOrDefault(File.Exists)</c>。文件夹对 File.Exists 恒为 false，解析为 null →
    /// 静默 return，即复现。不真开资源管理器、不碰剪贴板。
    /// </summary>
    internal string ProbeFolderOpenLocate(long entryId)
    {
        var card = _cards.FirstOrDefault(item => item.Id == entryId);
        if (card is null)
        {
            ReloadData();
            card = _cards.FirstOrDefault(item => item.Id == entryId);
        }

        if (card is null)
        {
            return "repro|card=False\n";
        }

        var resolved = Shiyu.Core.FileEntries.FirstAlive(card.Files)
            ?? (card.OriginalPath is { Length: > 0 } path && Shiyu.Core.FileEntries.Alive(path) ? path : null);
        var directoryAlive = card.Files.Any(System.IO.Directory.Exists);
        return FormattableString.Invariant(
            $"repro|kind={card.Kind}|resolved={resolved is not null}|directoryAlive={directoryAlive}|paths={string.Join(",", card.Files)}");
    }

    /// <summary>
    /// 探针命令 preview-pin 的管理窗一段（用户需求 2026-10-19：预览弹窗加钉，钉住不自动收起、
    /// 只留干净内容。验收用）：挑第一条卡片走真实悬停路径出预览 → 钉上 → 核对干净模式
    /// （元信息与教学行收起）→ 模拟策略收起被钉挡下 → 取消钉 → 收起。
    /// 判断在应用里做完，日志只有真假。
    /// </summary>
    internal async Task<string> ProbePreviewPin()
    {
        var log = new StringBuilder();
        var card = _cards.FirstOrDefault();
        if (card is null)
        {
            return "preview|card=False\n";
        }

        EnsurePreview().ShowFor(card, PlacementAnchor(AnchorFor(card) ?? WindowRect()), slide: false,
            scaleX: 1, scaleY: 1, band: ZBandPolicy.FollowsHost(Topmost));
        await Task.Delay(600);

        if (_preview is not { IsVisible: true } preview)
        {
            return "preview|notVisible\n";
        }

        var metaBefore = preview.ProbeMetaVisible;
        preview.ProbeClickPin();
        await Task.Delay(200);
        var pinned = preview.ProbePinned && preview.IsVisible;
        var clean = !preview.ProbeMetaVisible && !preview.ProbeTeachVisible;

        // 钉着：模拟策略收起（无 force 的 TakeDown 路径）——应被挡下。
        preview.TakeDown();
        await Task.Delay(300);
        var surviveTakeDown = preview.IsVisible;

        preview.ProbeClickPin();   // 取消钉 → 立即收起。
        await Task.Delay(400);
        var closedAfterUnpin = !preview.IsVisible;

        var previewLine =
            $"preview|metaBefore={metaBefore}|pinned={pinned}|clean={clean}"
            + $"|surviveTakeDown={surviveTakeDown}|closedAfterUnpin={closedAfterUnpin}";
        log.AppendLine(previewLine);
        return log.ToString();
    }

    /// <summary>贴边收起时的可见条高（DIP；用户需求 2026-10-20 的探针读数）。</summary>
    internal double ProbeVisibleStrip()
    {
        var workTop = SystemParameters.WorkArea.Top;
        return Math.Max(0, Top + ActualHeight - workTop);
    }

    /// <summary>贴边展开态的读数：没收着就是展开（用户需求 2026-10-20 的探针读数）。</summary>
    internal bool ProbeDockExpanded() => !_edgeCollapsed;

    /// <summary>探针：模拟鼠标进窗（贴边展开）。</summary>
    internal void ProbeDockMouseEnter() => OnDockMouseEnter(this, null!);

    /// <summary>探针：模拟鼠标离窗（贴边收回）。</summary>
    internal void ProbeDockMouseLeave() => OnDockMouseLeave(this, null!);

    /// <summary>窗口当前物理位置（整窗拖动的读数，用户实录 2026-10-20 的外框问题）。</summary>
    internal string ProbeWindowXY
        => Shiyu.Windows.WindowRects.TryGet(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var rect)
            ? FormattableString.Invariant($"{rect.Left};{rect.Top}")
            : "none";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private bool ProbeCursorToBar(double xDip, double yDip)
    {
        var screen = PointToScreen(new System.Windows.Point(xDip, yDip));
        return SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
    }

    private static async Task ProbePumpAsync()
    {
        for (var round = 0; round < 4; round++)
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    // 拖动用真实输入（SendInput 的左键按下 / 抬起）：合成的按下骗不过系统——真键没按着，捕获随即
    // 被收回、抬起被补发，拖动在探针里走不起来（2026-10-21 跟踪实录）。按下前确认光标下的根窗口
    // 就是这扇窄条，左右键没有对调：否则不按，免得点到用户的窗口。

    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct NativeInput
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public uint Type;

        [System.Runtime.InteropServices.FieldOffset(8)]
        public MouseInput Mouse;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private bool CursorOverThisBar()
        => GetCursorPos(out var at)
            && GetAncestor(WindowFromPoint(at), 2 /* GA_ROOT */) == new System.Windows.Interop.WindowInteropHelper(this).Handle;

    private static void SendLeft(uint flags)
        => SendInput(1, [new NativeInput { Type = InputMouse, Mouse = new MouseInput { Flags = flags } }],
            System.Runtime.InteropServices.Marshal.SizeOf<NativeInput>());

    /// <summary>品牌钮（窄条的拖动把手）中心，窗口 DIP 坐标。</summary>
    internal System.Windows.Point ProbeBrandCenter
        => BrandButton.TranslatePoint(new System.Windows.Point(BrandButton.ActualWidth / 2, BrandButton.ActualHeight / 2), this);

    /// <summary>真实左键按下（窗口 DIP 坐标）。光标下不是这扇窄条、或左右键对调时不按，返回假。</summary>
    internal async Task<bool> ProbeBarPressAtAsync(double xDip, double yDip)
    {
        if (GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 || !ProbeCursorToBar(xDip, yDip))
        {
            return false;
        }

        await ProbePumpAsync();
        if (!CursorOverThisBar())
        {
            return false;
        }

        SendLeft(MouseEventLeftDown);
        await SettleInputAsync();
        return true;
    }

    /// <summary>
    /// 等系统把真实输入送进来：ProbePumpAsync 让出的是 Render 优先级，比 Input 高，光是让出几拍
    /// 输入消息还排在后面，读到的是挪动之前的窗口位置。
    /// </summary>
    private static async Task SettleInputAsync()
    {
        await ProbePumpAsync();
        await Task.Delay(60);
    }

    /// <summary>按着键移动光标（窗口 DIP 坐标，相对窗口此刻的位置）：系统把移动送给持有捕获的窗。</summary>
    internal async Task<bool> ProbeBarMoveToAsync(double xDip, double yDip)
    {
        if (!ProbeCursorToBar(xDip, yDip))
        {
            return false;
        }

        await SettleInputAsync();
        return true;
    }

    /// <summary>真实左键抬起。光标已不在窄条上（窗口没跟过来）时先挪回把手再松开。</summary>
    internal async Task ProbeBarReleaseAsync()
    {
        await ProbePumpAsync();
        if (!CursorOverThisBar())
        {
            var brand = ProbeBrandCenter;
            ProbeCursorToBar(brand.X, brand.Y);
            await SettleInputAsync();
        }

        SendLeft(MouseEventLeftUp);
        await SettleInputAsync();
    }

    /// <summary>此刻真实光标的位置（探针收尾时放回去，免得下一轮开头被它触发悬停）。</summary>
    internal static (int X, int Y) ProbeCursorNow()
        => GetCursorPos(out var at) ? (at.X, at.Y) : (0, 0);

    internal static void ProbeCursorBack((int X, int Y) at) => SetCursorPos(at.X, at.Y);

    /// <summary>真实光标移到贴边细条的正中，停一会儿（鼠标进细条该弹出）。</summary>
    internal async Task ProbeHoverStripAsync()
    {
        ProbeCursorToBar(ActualWidth / 2, ActualHeight - DockedStrip / 2);
        await SettleInputAsync();
        await Task.Delay(700);
    }

    /// <summary>窄条上各种「快捷键」提示此刻的状态：Ctrl 键帽、卡片编号、悬停动作托盘、悬停预览、底栏提示。</summary>
    internal string ProbeHintState()
    {
        var badges = 0;
        var trays = 0;
        foreach (var container in RealizedContainers())
        {
            if (Tree.FindDescendant<RowKeyBadge>(container) is { IsVisible: true })
            {
                badges++;
            }

            if (Tree.FindDescendant<ActionTray>(container) is { IsVisible: true } tray && tray.Opacity > 0.01)
            {
                trays++;
            }
        }

        return FormattableString.Invariant(
            $"keys={_keyHintsOn},badges={badges},trays={trays},preview={_preview is { IsVisible: true }},footer={FeedbackHost.IsVisible},top={Top:F0}");
    }

    /// <summary>
    /// 真实光标停到按钮正中，再触发它的 Click——菜单照真实点击那样打开、抢走鼠标捕获，窗口照样报
    /// 「离开」，但不发真实按键。光标下的根窗口不是按钮所在的窗（被盖住、人在动鼠标）时不点，返回假：
    /// 光标不在窗上就没有那次「离开」，点了也什么都证明不了。
    /// </summary>
    internal async Task<bool> ProbePointAndClickAsync(ButtonBase target)
    {
        if (PresentationSource.FromVisual(target) is not System.Windows.Interop.HwndSource source)
        {
            return false;
        }

        var screen = target.PointToScreen(new System.Windows.Point(target.ActualWidth / 2, target.ActualHeight / 2));
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        await SettleInputAsync();
        if (!GetCursorPos(out var at) || GetAncestor(WindowFromPoint(at), 2 /* GA_ROOT */) != source.Handle)
        {
            return false;
        }

        target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, target));
        await SettleInputAsync();
        return true;
    }

    private List<(double At, double Move)>? _slideFrames;

    /// <summary>前三轮逐帧的「距上一帧 ms / 挪窗 ms」。</summary>
    private readonly List<string> _slideTrace = [];

    internal IReadOnlyList<string> ProbeSlideTrace => _slideTrace;

    partial void NoteSlideFrame(double atMs, double moveMs) => _slideFrames?.Add((atMs, moveMs));

    /// <summary>
    /// 量细条滑出的节奏（用户反馈 2026-10-22：弹出发卡）：收起、滑出，重复几轮，记下每帧的时刻与挪窗耗时。
    /// 返回一行读数：动画是否开着、平均每轮几帧、帧间隔的中位数 / 最大值、挪窗耗时的平均 / 最大值、整轮用时。
    /// </summary>
    internal async Task<string> ProbeMeasureSlideAsync(int rounds)
    {
        var intervals = new List<double>();
        var moves = new List<double>();
        var spans = new List<double>();
        var firstGaps = new List<double>();
        var firstMoves = new List<double>();
        var frames = 0;
        for (var round = 0; round < rounds; round++)
        {
            CollapseNow();
            await Task.Delay(300);
            _slideFrames = [];
            RestoreFromDock();
            await Task.Delay(500);
            var got = _slideFrames;
            _slideFrames = null;
            frames += got.Count;
            if (round < 3)
            {
                _slideTrace.Add(string.Join(",", got.Select((frame, index)
                    => FormattableString.Invariant($"{(index == 0 ? 0 : frame.At - got[index - 1].At):F0}/{frame.Move:F1}"))));
            }
            moves.AddRange(got.Select(frame => frame.Move));
            for (var index = 1; index < got.Count; index++)
            {
                intervals.Add(got[index].At - got[index - 1].At);
            }

            if (got.Count > 0)
            {
                spans.Add(got[^1].At);
                firstMoves.Add(got[0].Move);
            }

            if (got.Count > 1)
            {
                firstGaps.Add(got[1].At - got[0].At);
            }
        }

        static double Median(List<double> values)
            => values.Count == 0 ? 0 : values.OrderBy(value => value).ElementAt(values.Count / 2);

        return FormattableString.Invariant(
            $"slide|animated={Shiyu.Windows.UiAnimation.Allowed()}|frames={(double)frames / rounds:F1}|gapMedian={Median(intervals):F1}|gapMax={(intervals.Count > 0 ? intervals.Max() : 0):F1}|moveAvg={(moves.Count > 0 ? moves.Average() : 0):F2}|moveMax={(moves.Count > 0 ? moves.Max() : 0):F2}|span={Median(spans):F0}|firstGap={Median(firstGaps):F1}|firstMove={Median(firstMoves):F2}");
    }

    // --- bar-keys（用户实录 2026-10-22：选中卡片按 P 置顶，P 却打进了搜索框）------------------
    // 真实按键：只在这扇窄条就是前台窗口时才按，而且只按不会粘贴的键——字母动作、一个普通字母、↓、Esc。

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 40)]
    private struct NativeKeyInput
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public uint Type;

        [System.Runtime.InteropServices.FieldOffset(8)]
        public ushort Vk;

        [System.Runtime.InteropServices.FieldOffset(12)]
        public uint Flags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint SendKeys(uint count, NativeKeyInput[] inputs, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private bool OwnsForeground => GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(this).Handle;

    /// <summary>按下一组键（依次按下、倒序抬起）。窄条不是前台就不按，返回假。</summary>
    private async Task<bool> ProbeKeysAsync(params ushort[] chord)
    {
        if (!OwnsForeground)
        {
            return false;
        }

        var inputs = chord.Select(vk => new NativeKeyInput { Type = 1, Vk = vk })
            .Concat(chord.Reverse().Select(vk => new NativeKeyInput { Type = 1, Vk = vk, Flags = 2 /* KEYEVENTF_KEYUP */ }))
            .ToArray();
        SendKeys((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeKeyInput>());
        await SettleInputAsync();
        await Task.Delay(80);
        return true;
    }

    /// <summary>
    /// 走 WPF 自己的输入管线按一个键：PreviewKeyDown 由键盘设备送给此刻的焦点（没人处理就升成 KeyDown），
    /// 按下没被处理、这个键又出字时，再把这个字作为一次文字输入送给「那时」的焦点——真实的 WM_CHAR 就是
    /// 这样晚一步、按当时的焦点落地的。不碰前台窗口，人在用电脑也能跑。修饰键读的是真键盘，这里只能单按。
    /// </summary>
    private async Task<bool> ProbeSynthKeyAsync(Key key, string? text)
    {
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        InputManager.Current.ProcessInput(down);
        if (!down.Handled && text is not null && Keyboard.FocusedElement is { } focused)
        {
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, focused, text));
        }

        await SettleInputAsync();
        return down.Handled;
    }

    private bool ProbePinnedId(long id) => _pinned.Any(card => card.Id == id);

    private bool ProbeSelectId(long id)
    {
        if (VisibleRows.FirstOrDefault(card => card.Id == id) is not { } card)
        {
            return false;
        }

        Select(card);
        return true;
    }

    /// <summary>
    /// 探针命令 bar-keys：点一张卡（走 OnCardPressed）→ 按 P → 按 K → Esc → Ctrl+P → ↓ → S，逐步记下焦点、
    /// 搜索框内容、卡片的置顶与收藏。单按的键走合成的输入管线（<see cref="ProbeSynthKeyAsync"/>），Ctrl+P
    /// 走真键盘。输入法在这一轮关掉：字母要以原样到达，不进拼音组字。
    /// </summary>
    internal async Task<string> ProbeKeyRoutingAsync()
    {
        var log = new StringBuilder();
        InputMethod.SetIsInputMethodEnabled(this, false);
        InputMethod.SetIsInputMethodEnabled(SearchBox, false);
        try
        {
            if (_cards.FirstOrDefault() is not { } target
                || Cards.ItemContainerGenerator.ContainerFromItem(target) is not FrameworkElement container)
            {
                log.AppendLine("keys|card=False");
                return log.ToString();
            }

            var id = target.Id;
            log.AppendLine(FormattableString.Invariant($"start|focus={KeyFocus}|fg={OwnsForeground}|pinned={ProbePinnedId(id)}"));

            OnCardPressed(container, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = container,
            });
            await SettleInputAsync();
            log.AppendLine(FormattableString.Invariant($"click|focus={KeyFocus}"));

            await ProbeSynthKeyAsync(Key.P, "p");
            log.AppendLine(FormattableString.Invariant($"p|pinned={ProbePinnedId(id)}|query={SearchBox.Text.Length}|focus={KeyFocus}"));

            await ProbeSynthKeyAsync(Key.K, "k");
            log.AppendLine(FormattableString.Invariant($"k|focus={KeyFocus}|query={SearchBox.Text}"));

            // Ctrl+P 要真键盘（修饰键合成不了）：窄条不是前台就不按。
            await ProbeSynthKeyAsync(Key.Escape, null);
            ProbeSelectId(id);
            var pinnedBeforeCtrl = ProbePinnedId(id);
            if (await ProbeKeysAsync(0x11 /* Ctrl */, 0x50 /* P */))
            {
                log.AppendLine(FormattableString.Invariant(
                    $"ctrlp|before={pinnedBeforeCtrl}|pinned={ProbePinnedId(id)}|query={SearchBox.Text.Length}|focus={KeyFocus}"));
            }
            else
            {
                log.AppendLine("ctrlp|fg=False");
            }

            var favouriteBefore = VisibleRows.FirstOrDefault(card => card.Id == id)?.Favorite ?? false;
            await ProbeSynthKeyAsync(Key.Down, null);
            var focusAfterDown = KeyFocus;
            ProbeSelectId(id);
            await ProbeSynthKeyAsync(Key.S, "s");
            var favouriteAfter = VisibleRows.FirstOrDefault(card => card.Id == id)?.Favorite ?? false;
            log.AppendLine(FormattableString.Invariant(
                $"down|focus={focusAfterDown}|favoriteToggled={favouriteAfter != favouriteBefore}|query={SearchBox.Text.Length}"));
        }
        finally
        {
            InputMethod.SetIsInputMethodEnabled(this, true);
            InputMethod.SetIsInputMethodEnabled(SearchBox, true);
        }

        return log.ToString();
    }

    /// <summary>鼠标进细条却没弹出时，挡住它的几道闸此刻的状态。</summary>
    internal string ProbeDockGate()
        => FormattableString.Invariant(
            $"summoned={_summoned}|fullscreen={Shiyu.Windows.FullScreenOverlay.IsForeground()}|overBar={CursorOverThisBar()}|mouseOver={IsMouseOver}|active={EdgeDockActive}");

    internal ButtonBase ProbeTagFilterButton => TagFilter;

    internal ButtonBase ProbeSubtypeButton => SubtypeButton;

    internal bool ProbeMenuOpen => OpenMenu is not null;

    /// <summary>此刻开着的菜单里的第一项（标签下拉里是「全部」）。</summary>
    internal ButtonBase? ProbeOpenMenuFirstItem
        => OpenMenu?.Child is { } child ? Tree.FindDescendant<Button>(child) : null;

    internal void ProbeCloseMenu()
    {
        if (OpenMenu is { } menu)
        {
            menu.IsOpen = false;
        }
    }

    /// <summary>真实光标挪到窄条右侧（右边没屏了就左侧）的空处，只挪不点。</summary>
    internal void ProbeCursorBesideBar()
    {
        if (!Shiyu.Windows.WindowRects.TryGet(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var rect))
        {
            return;
        }

        var x = rect.Right + 80 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            ? rect.Right + 80
            : rect.Left - 80;
        SetCursorPos(x, rect.Top + 120);
    }

    /// <summary>拖到一半把鼠标捕获夺走（Alt+Tab、Win 键、窗被收起时系统就是这么做的）。</summary>
    internal void ProbeBarLoseCapture() => System.Windows.Input.Mouse.Capture(null);

    /// <summary>
    /// 探针钩子：模拟一次完整的拖动——按下时从细条拉出、窗口挪到落点（Top = topDip，按离工作区顶的
    /// 距离换到窗口所在屏）、松手走落点判定。
    /// </summary>
    internal void ProbeDragEndedAt(double topDip)
    {
        PullOutForDrag();
        ProbeParkAt(topDip);
        if (Shiyu.Windows.WindowRects.TryGet(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var after))
        {
            ApplyEdgeDockAfterDrag(after);
        }
    }

    /// <summary>只把窗口挪到 Top = topDip（不走拖动判定）：给后面的步骤摆好起点。</summary>
    internal void ProbeParkAt(double topDip)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!Shiyu.Windows.WindowRects.TryGet(handle, out var rect))
        {
            return;
        }

        var work = Shiyu.Windows.ScreenGeometry.WorkAreaAt(new ScreenPoint(rect.Left, Math.Max(rect.Top, rect.Bottom - 1)));
        var top = work.Top + (int)Math.Round(topDip - SystemParameters.WorkArea.Top);
        Shiyu.Windows.TransientWindow.MoveTo(handle, new ScreenPoint(rect.Left, top), ZBandPolicy.FollowsHost(Topmost));
    }
}
#endif
