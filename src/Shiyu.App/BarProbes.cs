#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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

        var display = PreviewSizing.ImageDisplay(card.PixelWidth, card.PixelHeight, scale);
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
}
#endif
