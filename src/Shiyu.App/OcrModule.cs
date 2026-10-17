using System.IO;
using System.Threading.Channels;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 图里的字（评审 2026-10-16 F-07）：新记下的图在后台认字，记进库、进全文索引，复制过的截图搜其中的词也搜得到；
/// 启动一分钟后，再把最近还没认过的图补上（一张一张来，每张之间歇一下，不和用户抢机器）。
///
/// 认出来的字过一遍排除名单的内容规则——排除名单说"这种东西别存"，截图里的也一样。全在线程池上，界面线程不碰。
/// 这台机器没有可用的识别语言就什么都不做。
/// </summary>
internal sealed class OcrModule
{
    /// <summary>启动后补认最近多少张：更早的图搜得少，留给用户需要时再说（「复制图中文字」会当场认）。</summary>
    private const int BackfillLimit = 200;

    private static readonly TimeSpan BackfillDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BackfillPace = TimeSpan.FromMilliseconds(500);

    private readonly Channel<(long Id, byte[]? Png, string? Path)> _queue =
        Channel.CreateUnbounded<(long, byte[]?, string?)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stop = new();
    private AppShell? _shell;

    public void Start(AppShell shell)
    {
        _shell = shell;
        if (!ImageText.Available)
        {
            return;
        }

        if (shell.Pipeline is { } pipeline)
        {
            pipeline.ImageRecorded += (id, png) => _queue.Writer.TryWrite((id, png, null));
        }

        _ = Task.Run(() => WorkAsync(_stop.Token));
        _ = Task.Run(() => BackfillAsync(_stop.Token));
    }

    public void Shutdown()
    {
        _stop.Cancel();
        _queue.Writer.TryComplete();
    }

    /// <summary>
    /// 窄条「复制图中文字」：认过就用存下的；还没认（新图还在排队、或更早的图）就当场认一次，认完也记进库。
    /// 认不了（没有识别语言、原图已按保留期清掉）为 null。
    /// </summary>
    public async Task<string?> TextOfAsync(long id)
    {
        var shell = _shell!;
        if (shell.Store.Get(id) is not { Kind: EntryKind.Image } entry)
        {
            return null;
        }

        if (entry.OcrText is { } known)
        {
            return known;
        }

        if (!ImageText.Available || entry.OriginalPath is not { } path || !File.Exists(path))
        {
            return null;
        }

        return await Task.Run(() => RecognizeAndKeepAsync(id, File.ReadAllBytes(path)));
    }

    private async Task WorkAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var (id, png, path) in _queue.Reader.ReadAllAsync(stop))
            {
                try
                {
                    var bytes = png ?? (path is not null && File.Exists(path) ? await File.ReadAllBytesAsync(path, stop) : null);
                    if (bytes is not null)
                    {
                        await RecognizeAndKeepAsync(id, bytes);
                    }

                    // 补认的旧图一张一张来，不和用户抢机器；刚复制的图不等。
                    if (png is null)
                    {
                        await Task.Delay(BackfillPace, stop);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception failure)
                {
                    // 一张图出问题不拖累后面的；原因留一行日志（不含内容）。
                    Log.Event(LogEvent.OcrFailed, failure, ("background", 1));
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // expected: 退出时收工。
        }
    }

    private async Task BackfillAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(BackfillDelay, stop);
            foreach (var (id, path) in _shell!.Store.ImagesAwaitingText(BackfillLimit))
            {
                _queue.Writer.TryWrite((id, null, path));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // expected: 还没到补认的时候就退出了。
        }
    }

    /// <summary>
    /// 认一张、记下。认不了（图坏了）也记成空串：坏图不该每次启动都再认一遍。命中排除名单内容规则的字不留。
    /// </summary>
    private async Task<string?> RecognizeAndKeepAsync(long id, byte[] png)
    {
        var shell = _shell!;
        var text = await ImageText.RecognizeAsync(png) ?? string.Empty;
        if (text.Length > 0 && shell.Exclusions.Excludes(new ClipboardSnapshot(text, null, false)))
        {
            text = string.Empty;
        }

        shell.Store.SetOcrText(id, text);
        return text;
    }
#if DEBUG
    /// <summary>
    /// 探针命令 ocr（评审 2026-10-16 F-07）：先画一张写着「拾语测试 12345」的 PNG，当场认一遍——字认得回来；
    /// 再开着输入优先级的探针（每 25 ms 投一个空活，量它等了多久）连认 6 张，界面线程不该被识别拖住。
    /// 判断在这里做完，日志只有真假与毫秒（脚本只认 ASCII）。
    /// </summary>
    internal async void ProbeOcr()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var png = RenderProbeImage("拾语测试 12345");

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var text = await ImageText.RecognizeAsync(png);
            watch.Stop();
            log.AppendLine(FormattableString.Invariant(
                $"real|available={ImageText.Available}|ok={text?.Contains("12345") == true && text!.Contains("测试")}|ms={watch.Elapsed.TotalMilliseconds:F0}"));

            var latencies = new List<double>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var stop = new CancellationTokenSource();
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var pinger = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var posted = clock.ElapsedTicks;
                    await dispatcher.InvokeAsync(
                        () => latencies.Add((clock.ElapsedTicks - posted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency),
                        System.Windows.Threading.DispatcherPriority.Input);
                    try { await Task.Delay(25, stop.Token); }
                    catch (OperationCanceledException) { }
                }
            });

            // 识别跑满一整个窗口（一张接一张），探针全程都在量——六张热图几十毫秒就完了，
            // 量不出任何东西（2026-10-17 首跑只等到 1 个 ping）。
            await Task.Run(async () =>
            {
                while (clock.ElapsedMilliseconds < 1200)
                {
                    await ImageText.RecognizeAsync(png);
                }
            });
            stop.Cancel();
            await pinger;

            latencies.Sort();
            log.AppendLine(FormattableString.Invariant(
                $"latency|pings={latencies.Count}|max={(latencies.Count > 0 ? latencies[^1] : 0):F1}"));
        }
        catch (Exception failure)
        {
            // expected: 探针把异常写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }

        log.AppendLine("done");
        System.IO.File.WriteAllText(System.IO.Path.Combine(DebugOverrides.ProbeDirectory!, "ocr.log"), log.ToString());
    }

    /// <summary>探针用的测试图：白底黑字一行，渲染成 PNG。</summary>
    private static byte[] RenderProbeImage(string line)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, 640, 160));
            var text = new System.Windows.Media.FormattedText(
                line,
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Microsoft YaHei"),
                48,
                System.Windows.Media.Brushes.Black,
                1.25);
            context.DrawText(text, new System.Windows.Point(24, 48));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(640, 160, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
#endif
}
