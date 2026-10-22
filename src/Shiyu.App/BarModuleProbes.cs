#if DEBUG
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>窄条模块的调试探针区（只在 Debug 构建存在，同 App.DebugProbes.cs）。</summary>
internal sealed partial class BarModule
{
    /// <summary>
    /// 探针命令 bar-newest 的入口（见 BarWindow.ProbeStartsAtNewest）。先钉住："人在
    /// 别处、条还开着"只有钉住的窄条才有。
    /// </summary>
    /// <summary>「拖到顶端」提示说过几次。</summary>
    internal int ProbeDockHints => _dockHints;

    internal void ProbeStartsAtNewest()
    {
        ProbePin();
        EnsureBar()?.ProbeStartsAtNewest();
    }

    /// <summary>
    /// 探针命令 preview-image 的入口（见 BarWindow.ProbePreviewImage）。先钉住：脚本
    /// 从外面按键，窄条不能因为一次失焦就收起。
    /// </summary>
    internal void ProbePreviewImage()
    {
        ProbePin();
        EnsureBar()?.ProbePreviewImage();
    }

    /// <summary>
    /// 探针命令 bar 及量窄条的探针（caret、content-size）：钉住后照快速粘贴呼出——
    /// 它们量的是一扇一直开着的窄条，旁边还会开别的窗（撤掉的常驻窄条原来的位置）。
    /// </summary>
    internal void ProbeShowPinned()
    {
        ProbePin();
        ShowQuickPaste();
    }

    /// <summary>探针命令 source-icons 的窄条一段（见 BarWindow.ProbeIconSlots）。</summary>
    internal string ProbeIconSlots(string phase)
        => EnsureBar()?.ProbeIconSlots(phase) ?? $"bar-{phase}|missing{Environment.NewLine}";

    /// <summary>探针命令 source-icons 的键帽一段（见 BarWindow.ProbeKeycap）。</summary>
    internal async Task<string> ProbeKeycap()
        => EnsureBar() is { } bar ? await bar.ProbeKeycap() : $"keycap|missing{Environment.NewLine}";

    /// <summary>
    /// 探针命令 bar-sharp 的入口（用户实录 2026-10-14：窄条中的图片显示模糊；见
    /// BarWindow.ProbeSharpImages）。开窄条之前先让最新那张图片的原图"过了保留期"——删掉文件，
    /// 它该留在缩略图上；其余还有原图的，上屏后该换上按屏幕像素解码的那张。
    /// </summary>
    internal void ProbeSharpImages()
    {
        var expired = _shell!.Store.ImagesWithOriginals()
            .OrderByDescending(entry => entry.CreatedAt)
            .FirstOrDefault();
        if (expired?.OriginalPath is { } path)
        {
            File.Delete(path);
        }

        ProbePin();
        ShowQuickPaste();
        EnsureBar()?.ProbeSharpImages(expired?.Id ?? -1);
    }

    /// <summary>经 store 钉住，和头部图钉同一条路（落盘的是探针自己的临时数据目录）。</summary>
    private void ProbePin() => _shell!.TryUpdateSettings(s => s with { BarPinned = true });

    /// <summary>
    /// 探针命令 bar-pin（用户需求 2026-10-10：撤掉常驻窄条，窄条右上角加常驻钉住与设置，
    /// 设置里加常驻钉住开关、默认关）。全走真实路径：托盘菜单没有「打开窄条」；默认没钉住，
    /// 点别处即收；点头部图钉经 store 钉住，点别处不收、仍在最前；点下没激活的窄条那一刻记下
    /// 贴回哪扇窗；钉住时再按快速粘贴，不挪、搜索清空；在设置里取消钉住、窄条又不是前台就
    /// 收起；齿轮打开设置的「窄条」页。一行一事写进 bar-pin.log，头部前后两态各渲染一张图。
    /// "点别处"由本进程的一扇小窗来抢焦点；前台锁不给焦点时，日志如实记下，脚本跳过那一项。
    /// </summary>
    internal async void ProbePinBehaviour()
    {
        var shell = _shell!;
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();

        static async Task Settle()
        {
            for (var round = 0; round < 4; round++)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
        }

        var elsewhere = new Window
        {
            Title = "probe-elsewhere",
            Width = 160,
            Height = 90,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 40,
            Top = 40,
        };

        async Task<bool> FocusElsewhere()
        {
            elsewhere.Show();
            elsewhere.Activate();
            await Settle();
            return elsewhere.IsActive;
        }

        // Row keys, not labels: the probe scripts stay ASCII.
        log.AppendLine("tray|rows=" + string.Join(",", TrayModule.MenuRows(shell.Settings)
            .Where(row => row.Key.Length > 0)
            .Select(row => row.Key)));

        // 1. The default: unpinned, the hollow pin in the quiet colour.
        var pinnedAtStart = shell.Settings.BarPinned;
        ShowQuickPaste();
        await Settle();
        if (_bar is not { } bar)
        {
            File.WriteAllText(Path.Combine(directory, "bar-pin.log"), "bar|missing" + Environment.NewLine + "done" + Environment.NewLine);
            return;
        }

        log.AppendLine(FormattableString.Invariant(
            $"default|pinned={pinnedAtStart}|glyph={bar.ProbePinGlyph:X4}|accent={bar.ProbePinAccented}|topmost={bar.Topmost}|visible={bar.IsVisible}"));
        log.AppendLine("header|" + bar.ProbeHeaderLayout());
        App.RenderToPng(bar.ProbeHeader, Path.Combine(directory, "bar-pin-header-off.png"));

        // 2. Unpinned, the user clicks elsewhere: gone.
        var activeBefore = bar.IsActive;
        var away = await FocusElsewhere();
        log.AppendLine(FormattableString.Invariant(
            $"unpinned-away|active-before={activeBefore}|elsewhere-active={away}|visible={bar.IsVisible}"));
        elsewhere.Hide();

        // 3. The header pin: one click, through the store, and the face follows.
        ShowQuickPaste();
        await Settle();
        bar.ProbeClickPin();
        await Settle();
        log.AppendLine(FormattableString.Invariant(
            $"pinned|setting={shell.Settings.BarPinned}|glyph={bar.ProbePinGlyph:X4}|accent={bar.ProbePinAccented}|topmost={bar.Topmost}"));
        App.RenderToPng(bar.ProbeHeader, Path.Combine(directory, "bar-pin-header-on.png"));

        // 4. Pinned, the user clicks elsewhere: it stays, in front.
        activeBefore = bar.IsActive;
        away = await FocusElsewhere();
        log.AppendLine(FormattableString.Invariant(
            $"pinned-away|active-before={activeBefore}|elsewhere-active={away}|visible={bar.IsVisible}|topmost={bar.Topmost}"));

        // 5. A press on the inactive pinned bar notes the window the user was in.
        var foreground = ForegroundWindow.Current().Handle;
        bar.ProbeMouseActivate();
        log.AppendLine(FormattableString.Invariant(
            $"return-target|foreground={foreground}|bar={bar.ProbeHandle}|elsewhere={new System.Windows.Interop.WindowInteropHelper(elsewhere).Handle}|noted={bar.ProbeReturnTarget}"));
        elsewhere.Hide();
        await Settle();

        // 6. Quick paste again over the open pinned bar: it stays where it is,
        // the session starts over, and the bar never notes itself as the target.
        var noted = bar.ProbeReturnTarget;
        bar.ProbeQuery = "probe query";
        await Settle();
        var (left, top) = (bar.Left, bar.Top);
        ShowQuickPaste();
        await Settle();
        log.AppendLine(FormattableString.Invariant(
            $"stay-put|before={left:F0},{top:F0}|after={bar.Left:F0},{bar.Top:F0}|query={bar.ProbeQuery.Length}|active={bar.IsActive}|self-noted={bar.ProbeReturnTarget == bar.ProbeHandle}|kept-target={bar.ProbeReturnTarget == noted}"));

        // 7. Unpinned from the settings page while the bar is not the
        // foreground: it goes, the way a focus loss would have taken it.
        away = await FocusElsewhere();
        var barActive = bar.IsActive;
        shell.TryUpdateSettings(s => s with { BarPinned = false });
        await Settle();
        log.AppendLine(FormattableString.Invariant(
            $"unpin-from-settings|elsewhere-active={away}|bar-active={barActive}|visible={bar.IsVisible}|setting={shell.Settings.BarPinned}|glyph={bar.ProbePinGlyph:X4}"));
        elsewhere.Close();

        // 8. The gear: settings open on the bar's page.
        ShowQuickPaste();
        await Settle();
        bar.ProbeClickSettings();
        await Settle();
        var settings = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault(window => window.IsVisible);
        log.AppendLine(FormattableString.Invariant(
            $"settings|opened={settings is not null}|page={SettingsWindow.ProbeCurrentPage}"));

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "bar-pin.log"), log.ToString());
    }

    /// <summary>
    /// 探针命令 summon-timing（评审 2026-10-16 F-21：复测热路径——ADR-0006 定义的真正指标）：进程常驻时，按下快速
    /// 粘贴到窄条可见、列表排好（界面线程闲下来）要多久。轻量模式开（隐藏即清卡片、收紧内存，下一次重建）与关
    /// 各量 20 次（先空跑一次），每次之间收起并停 400 ms，让轻量化做完。写下 P50 / P95 / 最大值（毫秒）。
    /// </summary>
    internal async void ProbeSummonTiming()
    {
        var shell = _shell!;
        var log = new StringBuilder();
        foreach (var lightweight in new[] { true, false })
        {
            shell.TryUpdateSettings(settings => settings with { LightweightWhenHidden = lightweight, BarPinned = false });
            await Task.Delay(300);

            var times = new List<double>();
            for (var run = 0; run <= 20; run++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                ShowQuickPaste();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                if (run > 0)
                {
                    times.Add(elapsed);
                }

                _bar?.Dismiss();
                await Task.Delay(400);
            }

            times.Sort();
            double At(double share) => times[Math.Min(times.Count - 1, (int)Math.Ceiling(share * times.Count) - 1)];
            log.AppendLine(FormattableString.Invariant(
                $"{(lightweight ? "lightweight" : "steady")}|runs={times.Count}|p50={At(0.5):F0}|p95={At(0.95):F0}|max={times[^1]:F0}"));
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(DebugOverrides.ProbeDirectory!, "summon-timing.log"), log.ToString());
    }
    /// <summary>探针命令 preview-pin 的窄条一段（见 BarWindow.ProbePreviewPin）。</summary>
    internal async Task<string> ProbePreviewPin()
        => _bar is null ? "preview|bar-missing\n" : await _bar.ProbePreviewPin();

    /// <summary>探针命令 preview-workbench 的窄条一段（见 BarWindow.ProbeWorkbenchMatrixAsync）。</summary>
    internal async Task<string> ProbeWorkbenchMatrix()
        => _bar is null ? "w|bar-missing\n" : await _bar.ProbeWorkbenchMatrixAsync();

    /// <summary>探针命令 preview-anchor 的窄条一段（见 BarWindow.ProbeAnchorAsync）。</summary>
    internal async Task<string> ProbeAnchor()
        => _bar is null ? "anchor|bar-missing\n" : await _bar.ProbeAnchorAsync();

    /// <summary>探针命令 preview-zoom-perf 的窄条一段（见 BarWindow.ProbeZoomPerfAsync）。</summary>
    internal async Task<string> ProbeZoomPerf()
        => _bar is null ? "zoom|bar-missing\n" : await _bar.ProbeZoomPerfAsync();

    /// <summary>探针命令 preview-decode-cost 的窄条一段（见 BarWindow.ProbeDecodeCostAsync）。</summary>
    internal async Task<string> ProbeDecodeCost()
        => _bar is null ? "decode|bar-missing\n" : await _bar.ProbeDecodeCostAsync();

    /// <summary>探针命令 preview-churn 的窄条一段（见 BarWindow.ProbeChurnAsync）。</summary>
    internal async Task<string> ProbeChurn()
        => _bar is null ? "churn|bar-missing\n" : await _bar.ProbeChurnAsync();
}
#endif
