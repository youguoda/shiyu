using Shiyu.Core;
using Shiyu.Windows;

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
        if (SelectionBadgeFilter.JudgeBeforeCapture(
                shell.Settings.SelectionBadge, DesktopShell.IsForeground())
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

        var deferred = capture.CaptureDeferRestore();
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

        if (!result.ClipboardRestored)
        {
            // The one failure worth interrupting the user for: their own
            // clipboard is gone and they would otherwise find out by pasting
            // the wrong thing somewhere that matters.
            shell.Tray.ShowNotification("拾语", "取词后未能还原你原本的剪贴板内容。");
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
    /// 任何可能抛出的清理之前。</summary>
    public void SettleOnExit() => Settle(_selectionDebt.Exit());

    /// <summary>执行账本的裁决：每笔债按序交还原，成败照旧转告用户。</summary>
    private void Settle(SelectionDebtAction action)
    {
        foreach (var debt in action.Restore)
        {
            RestoreDeferred(debt);
        }
    }

    /// <summary>
    /// 归还划词借走的剪贴板。还原失败是唯一值得打断用户的失败——他们的
    /// 剪贴板没了，不说话他们只会从粘贴错东西的那一刻才发现。
    /// </summary>
    private void RestoreDeferred(DeferredCapture deferred)
    {
        var shell = _shell!;
        if (shell.Capture is not null && !shell.Capture.Restore(deferred))
        {
            Log.Event(LogEvent.ClipboardRestoreFailed);
            shell.TellUser("取词后未能还原你原本的剪贴板内容。");
        }
    }

    /// <summary>徽标窗常驻复用；退出时按原样 CloseForGood。</summary>
    public void Shutdown() => _badge?.CloseForGood();
}
