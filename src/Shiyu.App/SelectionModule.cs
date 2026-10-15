using System.Windows.Threading;
using Shiyu.Core;
using Shiyu.Windows;
#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#endif

namespace Shiyu.App;

/// <summary>
/// 划词取词模块（O-40 拆自 App.xaml.cs）：拖选的裁决与徽标、"借出—还原"
/// 的账本（<see cref="SelectionDebt"/>——票 16 下沉 Core 的状态机在这里
/// 接线），以及两条显式取词入口（划词热键、翻译剪贴板）。
/// </summary>
internal sealed class SelectionModule
{
    private AppShell? _shell;
    private BadgeWindow? _badge;

    /// <summary>
    /// 划词"借出—还原"的账本（O-27 下沉候选 2）：取词借走用户剪贴板后，
    /// 还原被推迟到面板显示或徽标淡出（票 37）。何时还、还哪笔，由这台
    /// Core 状态机裁定并恰好结算一次；本模块只把它的裁决交给
    /// <see cref="RestoreDeferred"/> 执行。
    /// </summary>
    private readonly SelectionDebt _selectionDebt = new();

    /// <summary>
    /// 账本裁定到期的债交给它还（用户实录 2026-10-15：开着拖选徽标时常弹「取词后未能还原你原本的
    /// 剪贴板内容」）：剪贴板被别的程序占着就排着队、隔一会儿再试，按次序还，试满了还不行才告诉
    /// 用户。取词平台就位（<see cref="AppShell.AttachCapture"/>）之后、第一笔债到期时才建。
    /// </summary>
    private RestoreQueue? _restores;

    /// <summary>还原队列约下的下一回（UI 线程上，第一次要等时才建）。</summary>
    private DispatcherTimer? _restoreRetry;

    /// <summary>"前台在排除名单"的提示是否已经说过一次（O-17）。</summary>
    private bool _captureExcludedNotified;

    public void Attach(AppShell shell) => _shell = shell;

    /// <summary>
    /// One badge window, reused. It appears many times an hour; building a
    /// window each time is work the user would feel.
    ///
    /// 复制路径与划词路径共用这一扇窗：划词取词借走的剪贴板作为
    /// <paramref name="selection"/> 记入账本——新 Offer 接手前先结算上一笔，
    /// 徽标换主，旧账要清（复制徽标顶替旧划词徽标时同样清账）。
    /// </summary>
    public void ShowBadge(string text, DeferredCapture? selection = null)
    {
        // 徽标出现的这一刻：用户还要花一两秒才会点它，趁这段空档把免费引擎的会话取好。
        WarmFreeEngine();

        if (_badge is null)
        {
            _badge = new BadgeWindow();
            _badge.Accepted += OnBadgeAccepted;
            _badge.Dismissed += FlushPendingSelection;
        }

        Settle(selection is { } debt ? _selectionDebt.Offer(debt) : _selectionDebt.Dismissed());
        _badge.Offer(text);
    }

    /// <summary>
    /// 徽标被点击：复制徽标直达面板；划词徽标在面板显示之后再还原剪贴板
    /// （票 37 的 Glossy 次序——还原可以等，点击到出面板这一段不该再添
    /// 一次剪贴板写）。债在此刻转手给面板，之后迟到的淡出事件不会重复还。
    /// </summary>
    private void OnBadgeAccepted(string text)
    {
        Settle(_selectionDebt.Accepted());
        _shell!.ShowPanel?.Invoke(text, () => Settle(_selectionDebt.PanelDisplayed()));
    }

    /// <summary>
    /// 一次拖选完成（UI 线程上）：过滤链前段（总开关 → 桌面早退）拦下不值得
    /// 取词的场合；然后借出剪贴板模拟 Ctrl+C；再由后段（最小长度 → 须含
    /// 字母 → 拒绝路径形）裁决徽标。通过则徽标浮现，借走的剪贴板挂起，等
    /// 面板显示之后或徽标淡出时归还。
    ///
    /// 一切失败都安静收场：拖选是被动遭遇，不是用户点名的动作，安静的
    /// 没有徽标就是全部该有的反馈（划词热键路径保留着它的通知，那是显式
    /// 请求该有的待遇）。
    /// </summary>
    public void OnDragSelected()
    {
        var shell = _shell!;
        var capture = shell.Capture;
        if (capture is null)
        {
            return;
        }

        // 总开关传实时值：装钩与事件抵达之间设置若被关掉，这里也拦得住。
        // 「翻译徽标」与拖选开关两层都算（DragSelectBadge），关掉哪一层都拦。
        if (SelectionBadgeFilter.JudgeBeforeCapture(
                shell.Settings.DragSelectBadge, DesktopShell.IsForeground())
            != SelectionBadgeVerdict.Offer)
        {
            return;
        }

        // 排除名单是唯一闸口（ADR-0007），划词也必须过它（O-17）：向密码
        // 管理器模拟 Ctrl+C 去"取选中内容"，取到的就是密码本身。被动遭遇，
        // 安静地不出徽标就是全部该有的反应。
        if (CaptureGate.BlocksForeground(
                shell.Exclusions, ForegroundApplication.Current().Name))
        {
            return;
        }

        // 新一次取词前先还上一笔——两次快速拖选时，第二笔会借走第一笔的
        // 选中文字，不还就永远找不回用户最初的剪贴板。
        FlushPendingSelection();

        // 还有债排着队等重试（剪贴板正被别的程序占着）：这一回不借。借了，借走的是还没还回去的那段
        // 选中文字；旧债再还时认不出剪贴板，就当用户复制过而销账，用户的原文就找不回了。
        if (_restores?.Owing == true)
        {
            return;
        }

        // 剪贴板里是写不回去的东西（截图、剪切的文件、密码）就不借：被动遭遇不该让用户赔上剪贴板。
        var deferred = capture.CaptureDeferRestore(requireRestorable: true);
        if (deferred is null)
        {
            return;
        }

        if (deferred.Outcome != CaptureOutcome.Captured
            || SelectionBadgeFilter.JudgeCapturedText(deferred.Text!)
                != SelectionBadgeVerdict.Offer)
        {
            RestoreDeferred(deferred);
            return;
        }

        ShowBadge(deferred.Text!, deferred);
    }

    /// <summary>Captures what is selected in the foreground application and translates it.</summary>
    public void TranslateSelection()
    {
        var shell = _shell!;
        var capture = shell.Capture;
        if (capture is null || shell.Tray is null)
        {
            return;
        }

        // 热键按下的这一刻就开始预热：取词（模拟 Ctrl+C、等剪贴板、还原）要几百毫秒，
        // 够把免费引擎的会话取好。
        WarmFreeEngine();

        // 显式请求也要过闸口（O-17）：热键可以在任何前台应用按下，包括
        // 排除名单里的。说一声但只说一次——用户多半是忘了规则，每次取词
        // 都弹就成了骚扰。
        if (CaptureGate.BlocksForeground(
                shell.Exclusions, ForegroundApplication.Current().Name))
        {
            if (!_captureExcludedNotified)
            {
                _captureExcludedNotified = true;
                shell.Tray.ShowNotification("拾语", "前台应用在排除名单里，已跳过取词。");
            }

            return;
        }

        var result = capture.Capture();

        if (RestoreFailure(result.Restore) is { } failure)
        {
            // The one failure worth interrupting the user for: their own
            // clipboard is gone and they would otherwise find out by pasting
            // the wrong thing somewhere that matters. The translation they
            // asked for still opens — losing it too would help nobody.
            Log.Event(LogEvent.ClipboardRestoreFailed, ("hotkey", true), ("busy", result.Restore == CaptureRestore.Busy));
            shell.Tray.ShowNotification("拾语", failure);
            if (result.Succeeded)
            {
                shell.ShowPanel?.Invoke(result.Text!, null);
            }

            return;
        }

        switch (result.Outcome)
        {
            case CaptureOutcome.Captured:
                shell.ShowPanel?.Invoke(result.Text!, null);
                break;

            case CaptureOutcome.NothingCaptured:
                // All three causes look identical from out here, so the message
                // names them rather than asserting one. The third is the one a
                // user would never guess: a window running as administrator
                // silently discards synthesised keystrokes from a program that
                // is not, so capture simply never gets an answer.
                shell.Tray.ShowNotification(
                    "拾语",
                    "没有取到文字。可能是没有选中内容、该程序响应太慢，"
                    + "或它以管理员身份运行——那种窗口会丢弃拾语发出的按键。"
                    + "可以复制后按翻译剪贴板的快捷键。");
                break;

            case CaptureOutcome.ClipboardUnavailable:
                shell.Tray.ShowNotification("拾语", "剪贴板正被其他程序占用，稍后再试。");
                break;
        }
    }

    /// <summary>
    /// The escape hatch: translate whatever is on the clipboard right now,
    /// whether or not the badge ever offered to.
    /// </summary>
    public void TranslateClipboard()
    {
        var shell = _shell!;
        var capturePlatform = shell.CapturePlatform;
        if (capturePlatform is null || shell.Tray is null)
        {
            return;
        }

        WarmFreeEngine();

        string? text;
        try
        {
            text = capturePlatform.ReadClipboardText();
        }
        catch (ClipboardUnavailableException)
        {
            shell.Tray.ShowNotification("拾语", "剪贴板正被其他程序占用，稍后再试。");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            shell.Tray.ShowNotification("拾语", "剪贴板里没有可翻译的文字。");
            return;
        }

        // Deliberately not passed through the badge's filter: insisting is the
        // entire point of this hotkey.
        shell.ShowPanel?.Invoke(text, null);
    }

    /// <summary>
    /// 免费引擎的预热（票 41）：当前翻译方式是免费引擎、必应会话缺失或快过期时，在后台取一次。
    /// 不设定时器，失败不提示；其它翻译方式下什么都不做——不替用户联系微软。
    /// </summary>
    private void WarmFreeEngine() => FreeEngineBackend.WarmUp(_shell!.Settings);

    /// <summary>还掉挂起的划词剪贴板（若有）。幂等：没债就是空操作。</summary>
    public void FlushPendingSelection() => Settle(_selectionDebt.Dismissed());

    /// <summary>OnExit 的第一件事（O-05 修正）：账本把在册的债一次结清（新债
    /// 先还、旧债压轴，最终留在剪贴板里的是用户取词前的原文）——必须排在
    /// 任何可能抛出的清理之前。退出后没有下一回定时器，排着队的当场还完
    /// （撞上剪贴板被占着就在这里等一等）。</summary>
    public void SettleOnExit()
    {
        Settle(_selectionDebt.Exit());
        _restoreRetry?.Stop();
        if (_restores is { } restores)
        {
            Report(restores.Drain(Thread.Sleep));
        }
    }

    /// <summary>执行账本的裁决：每笔债按序交还原，成败照旧转告用户。</summary>
    private void Settle(SelectionDebtAction action)
    {
        foreach (var debt in action.Restore)
        {
            RestoreDeferred(debt);
        }
    }

    /// <summary>
    /// 归还划词借走的剪贴板：交给还原队列。撞上剪贴板被别的程序占着不算失败，隔一会儿再试
    /// （用户实录 2026-10-15：原来一撞上就报「未能还原」）。
    /// </summary>
    private void RestoreDeferred(DeferredCapture deferred)
    {
        if (_shell!.Capture is not { } capture)
        {
            return;
        }

        _restores ??= new RestoreQueue(capture.Restore, CaptureTiming.Default.RestoreRetryDelays);
        Apply(_restores.Owe(deferred));
    }

    /// <summary>还原队列这一回的结果：结清的照实转告，还有没还上的就约下一回。</summary>
    private void Apply(RestoreProgress progress)
    {
        Report(progress.Settled);

        if (progress.RetryAfter is not { } delay)
        {
            return;
        }

        if (_restoreRetry is null)
        {
            _restoreRetry = new DispatcherTimer();
            _restoreRetry.Tick += (_, _) =>
            {
                _restoreRetry.Stop();
                if (_restores is { } restores)
                {
                    Apply(restores.Retry());
                }
            };
        }

        _restoreRetry.Stop();
        _restoreRetry.Interval = delay;
        _restoreRetry.Start();
    }

    /// <summary>
    /// 还不上是唯一值得打断用户的失败——他们的剪贴板没了，不说话他们只会从粘贴错东西的那一刻
    /// 才发现。
    /// </summary>
    private void Report(IEnumerable<SettledRestore> settled)
    {
        foreach (var restore in settled)
        {
            if (RestoreFailure(restore.Result) is { } failure)
            {
                Log.Event(LogEvent.ClipboardRestoreFailed, ("busy", restore.Result == CaptureRestore.Busy));
                _shell!.TellUser(failure);
            }
        }
    }

    /// <summary>
    /// 还不上时对用户说的话。还上了、剪贴板根本没动过、被用户新复制的东西顶掉了、故意不写回的
    /// 密码，都不打扰（null）。
    /// </summary>
    private static string? RestoreFailure(CaptureRestore result) => result switch
    {
        CaptureRestore.Busy => "剪贴板一直被别的程序占着，取词后没能放回你原来的内容。通常还能在剪贴板历史里找到。",
        CaptureRestore.Unrestorable => "剪贴板里原来的截图之类没法原样放回，现在是取到的文字。通常还能在剪贴板历史里找到。",
        _ => null,
    };

    /// <summary>徽标窗常驻复用；退出时按原样 CloseForGood。</summary>
    public void Shutdown() => _badge?.CloseForGood();

#if DEBUG
    // --- 实机探针（用户实录 2026-10-15）：tools/probes/probe-capture-restore.ps1 -------------------

    /// <summary>
    /// 「取词后未能还原你原本的剪贴板内容」的实机验收。脚本先把记事本放到前台、选中全文，在剪贴板上
    /// 放一段已知的原文；这里依次跑四个场景，把看到的写进 capture-restore.log。"别的程序正读着剪贴板"
    /// 由本进程另一条线程扮演：它开着剪贴板不放，拾语的消息窗就打不开——和别的进程占着一样。
    /// </summary>
    internal async void ProbeCaptureRestore()
    {
        var shell = _shell!;
        var platform = shell.CapturePlatform!;
        var writer = shell.Writer!;
        var log = new StringBuilder();

        // 探针的种子设置里徽标关着；拖选路径看的是实时值。
        shell.TryUpdateSettings(settings => settings with { TranslateBadge = true, SelectionBadge = true });
        await Task.Delay(800);

        var original = await ReadPatiently(platform);
        log.AppendLine($"start|original={original is { Length: > 0 }}");

        // 1. 拖选取词；徽标淡出的那一刻，剪贴板正被别的程序占着 700 毫秒。
        OnDragSelected();
        var selection = await ReadPatiently(platform);
        var badge = _badge?.IsVisible == true;
        var held = await HoldClipboard(TimeSpan.FromMilliseconds(700));
        FlushPendingSelection();
        var waiting = _restores?.Owing == true;
        await Task.Delay(3000);
        var afterFade = await ReadPatiently(platform);
        log.AppendLine(
            $"fade|badge={badge}|captured={selection is { Length: > 0 } && selection != original}|held={held}"
            + $"|waiting={waiting}|owingAfter={_restores?.Owing == true}|restored={afterFade == original}");

        // 2. 拖选之后自己按 Ctrl+C：淡出时不许把这一下复制撤掉。
        OnDragSelected();
        await Task.Delay(300);
        platform.SendCopyKeystroke();
        await Task.Delay(600);
        FlushPendingSelection();
        await Task.Delay(1500);
        var afterOwnCopy = await ReadPatiently(platform);
        log.AppendLine($"own-copy|kept={afterOwnCopy == selection}");

        // 3. 剪贴板里是一张截图：拖选不借它，不发 Ctrl+C，截图原封不动。
        var picture = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var pictured = await Patiently(() =>
        {
            System.Windows.Clipboard.SetImage(picture);
            return true;
        });
        await Task.Delay(500);
        var beforeDrag = platform.ClipboardSequenceNumber();
        OnDragSelected();
        await Task.Delay(800);
        var untouched = platform.ClipboardSequenceNumber() == beforeDrag;
        var stillPicture = await Patiently(System.Windows.Clipboard.ContainsImage);
        log.AppendLine($"image|set={pictured}|untouched={untouched}|kept={stillPicture}");

        // 4. 划词热键那条路（取完立刻还），两个模拟的读者在每次变化后抢着开剪贴板。同样的场面
        //    先不重试跑一组（原来就是只试一回），再按 CaptureTiming 的重试跑一组。
        using (var contender = new ClipboardContender())
        {
            var single = new SelectionCapture(platform, CaptureTiming.Default with { RestoreRetryDelays = [] });
            foreach (var (name, subject) in new[] { ("single", single), ("retry", shell.Capture!) })
            {
                int captured = 0, failed = 0, wrong = 0;
                for (var round = 0; round < ProbeRounds; round++)
                {
                    await Task.Delay(700);
                    await Patiently(() => writer.SetText(original ?? string.Empty));
                    await Task.Delay(700);

                    var result = subject.Capture();
                    captured += result.Succeeded ? 1 : 0;
                    failed += result.ClipboardRestored ? 0 : 1;
                    wrong += await ReadPatiently(platform) == original ? 0 : 1;
                }

                log.AppendLine(
                    $"hotkey-{name}|rounds={ProbeRounds}|captured={captured}|restoreFailed={failed}|clipboardWrong={wrong}|holds={contender.Holds}");
            }
        }

        await Patiently(() => writer.SetText(original ?? string.Empty));
        log.AppendLine("done");
        File.WriteAllText(Path.Combine(DebugOverrides.ProbeDirectory!, "capture-restore.log"), log.ToString());
    }

    private const int ProbeRounds = 8;

    private static async Task<string?> ReadPatiently(ICapturePlatform platform)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return platform.ReadClipboardText();
            }
            catch (ClipboardUnavailableException) when (attempt < 60)
            {
                // expected: 模拟的读者正占着剪贴板，等它放手再读。
                await Task.Delay(50);
            }
        }
    }

    private static async Task<bool> Patiently(Func<bool> attempt)
    {
        for (var tries = 0; tries < 60; tries++)
        {
            try
            {
                if (attempt())
                {
                    return true;
                }
            }
            catch (COMException)
            {
                // expected: WPF 的剪贴板调用在剪贴板被占着时抛 CLIPBRD_E_CANT_OPEN，等一等再试。
            }

            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>另一条线程打开剪贴板、占住 <paramref name="duration"/>；打开了才回来（true）。</summary>
    private static Task<bool> HoldClipboard(TimeSpan duration)
    {
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (ProbeClipboard.OpenClipboard(IntPtr.Zero))
                {
                    opened.SetResult(true);
                    Thread.Sleep(duration);
                    ProbeClipboard.CloseClipboard();
                    return;
                }

                Thread.Sleep(5);
            }

            opened.SetResult(false);
        })
        { IsBackground = true, Name = "probe clipboard holder" }.Start();
        return opened.Task;
    }

    /// <summary>
    /// 抢着读剪贴板的别的程序（Windows 剪贴板历史、输入法、剪贴板工具）：剪贴板一变，隔几毫秒就打开它
    /// 占一阵，前后两回，像两个各读各的监听者。种子固定，两组撞上的是同一路数的占用。
    /// </summary>
    private sealed class ClipboardContender : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private int _holds;

        public ClipboardContender()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "probe clipboard contender" };
            _thread.Start();
        }

        public int Holds => Volatile.Read(ref _holds);

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join(TimeSpan.FromSeconds(2));
            _stop.Dispose();
        }

        private void Run()
        {
            var random = new Random(15);
            var seen = ProbeClipboard.GetClipboardSequenceNumber();
            while (!_stop.IsCancellationRequested)
            {
                Thread.Sleep(2);
                var now = ProbeClipboard.GetClipboardSequenceNumber();
                if (now == seen)
                {
                    continue;
                }

                // 不在占用之后重读序列号：占着的时候又变了，下一圈照样再抢一回，像排队的通知。
                seen = now;
                Thread.Sleep(random.Next(0, 30));
                Hold(random.Next(60, 260));
                Thread.Sleep(random.Next(20, 60));
                Hold(random.Next(40, 160));
            }
        }

        private void Hold(int milliseconds)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (ProbeClipboard.OpenClipboard(IntPtr.Zero))
                {
                    Interlocked.Increment(ref _holds);
                    Thread.Sleep(milliseconds);
                    ProbeClipboard.CloseClipboard();
                    return;
                }

                Thread.Sleep(2);
            }
        }
    }

    private static class ProbeClipboard
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();
    }
#endif
}
