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
    /// 动作——收起重开（轻量开 / 关）、条开着人在别处时来一条外部写、以粘贴
    /// 模式呼出、人在条里时来一条外部写——逐条把滚动状态记进数据目录的
    /// bar-newest.log，探针脚本据此判定。外部写从线程池发出，与剪贴板流水线
    /// 同一条跨线程的路。
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

        Summon();
        await Settle();

        // 1. Hide and summon again, lightweight teardown on (the default).
        var before = await ScrollToBottom();
        Dismiss();
        await Settle();
        Summon();
        await Settle();
        Note("reopen", before);

        // 2. The same with the teardown off: cards and offset survive the hide.
        _refreshPolicy.ApplySettings(lightweightWhenHidden: false);
        before = await ScrollToBottom();
        Dismiss();
        await Settle();
        Summon();
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

        // 4. The paste-mode summon over the open resident bar.
        before = await ScrollToBottom();
        SummonForPaste();
        await Settle();
        Note("paste-summon", before);
        Dismiss();
        await Settle();

        // 5. In use: the bar holds the focus — the place must stay put. The
        // log carries whether the focus was really ours; the script skips
        // the check when the foreground lock said no.
        Summon();
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

        Summon();
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
}
#endif
