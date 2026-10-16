using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 托盘与托盘菜单（O-40 拆自 App.xaml.cs）：托盘的建造、菜单事件的转达，
/// 以及托盘起来那一刻要说的两句话——坏设置文件的隔离通知、公共通道未上
/// 线的存量迁移提示。
///
/// 菜单结构按 UI 报告 §5.2 重排（票 25）：动作 + KeyMap 渲染的加速键列。
/// 曾经最显眼的 10 行灰显最近剪贴板内容整个退役——它点不了，在共享屏幕
/// 时还是一份意外的泄露清单。
/// </summary>
internal sealed class TrayModule
{
    /// <summary>菜单行的稳定标识（回传事件的 Key）。</summary>
    private const string QuickPasteKey = "quick-paste";
    private const string TranslateClipboardKey = "translate-clipboard";
    private const string ReverseInputKey = "reverse-input";
    private const string LibraryKey = "library";
    private const string SettingsKey = "settings";
    private const string KeymapKey = "keymap";
    private const string Pause15Key = "pause-15";
    private const string Pause60Key = "pause-60";
    private const string PauseKey = "pause";
    private const string ResumeKey = "resume";
    private const string UpdateKey = "update";
    private const string QuitKey = "quit";

    /// <summary>定时暂停到点时恢复记录（评审 2026-10-16 F-06）；记录本身按复制那一刻判定，不等它。</summary>
    private DispatcherTimer? _resume;

    /// <summary>
    /// Builds the tray and wires its menu. <paramref name="quarantineNotice"/>
    /// is the one sentence the settings load deferred until a tray existed to
    /// say it in — said once, then dropped.
    /// </summary>
    public void Attach(AppShell shell, string? quarantineNotice)
    {
        var tray = new TrayIcon(shell.MessageWindow, "拾语")
        {
            // 每次右键都重建：设置里改了键，下一次点开就是新的列；暂停与否也跟着现状走。
            Menu = () => MenuRows(shell.Settings, shell.RecordingPause.IsPaused(DateTimeOffset.Now)),
        };

        shell.Tray = tray;

        // 暂停记录（评审 2026-10-16 F-06）：图标换灰、提示文字写着到几点；定时的到点自动恢复并说一声。
        var pause = shell.RecordingPause;
        pause.Changed += () => ShowPauseState(tray, pause);

        // The menu is the manual entrance to everything the hotkeys also reach:
        // each item goes through the shell's relay slots, never at a module.
        tray.Command += key => Run(shell, key);

        // 点应用图标进设置（用户需求 2026-10-05，此前进管理窗）：拾语平时
        // 没有窗口，点它就是来调它。管理窗仍在右键菜单里，也是窄条品牌钮
        // 与自己的热键的去处。再次启动拾语（任务栏固定图标、开始菜单）走
        // 同一个去处，见 App 的 AnotherInstanceStarted。
        tray.Clicked += () => shell.ShowSettings?.Invoke();

        // An unparseable settings file was renamed aside, not overwritten:
        // that deserves one honest sentence once a tray exists to say it in.
        if (quarantineNotice is { } notice)
        {
            tray.ShowNotification("拾语", notice);
        }

        // 公共通道未上线的存量迁移（票 08/ADR-0009）：选中公共通道的用户
        // 只提示这一次——有自备密钥就切过去；没有就保留选择（设置里显示为
        // 「自备密钥」，评审 2026-10-16 F-05），翻译时由面板给配置引导卡。走 store 增量写。
        var settings = shell.Settings;
        if (settings.TranslationBackend == TranslationBackendKind.Relay
            && !settings.RelayUnavailableNoticed)
        {
            var hasOwnKey = settings.Backend.IsConfigured;
            if (shell.TryUpdateSettings(s => s with
            {
                TranslationBackend = hasOwnKey
                    ? TranslationBackendKind.OwnKey
                    : s.TranslationBackend,
                RelayUnavailableNoticed = true,
            }))
            {
                tray.ShowNotification("拾语", hasOwnKey
                    ? "公共翻译通道还未开放，已改用你自己的密钥翻译。"
                    : "公共翻译通道还未开放；可在 设置 → 翻译 改用免费引擎（无需账号），或配置自己的密钥（有免费的预设可选）。");
            }
        }
    }

    /// <summary>
    /// §5.2 的菜单结构：三个有键的动作（票 43 起含反向输入；用户需求 2026-10-10 撤掉
    /// 「打开窄条」，窄条只从快速粘贴进）/ 分隔 / 管理历史（设了键才带列）、
    /// 设置、键位速查 / 分隔 / 暂停记录（评审 2026-10-16 F-06：暂停着就只剩「恢复记录」）/
    /// 分隔 / 检查更新、退出。标签与加速键全部由 <see cref="KeyMap"/> 从现设置渲染——改键即改菜单。
    /// </summary>
    internal static IReadOnlyList<TrayMenuRow> MenuRows(AppSettings settings, bool paused = false)
    =>
    [
        new(QuickPasteKey, KeyMap.TrayLabel(HotkeyAction.QuickBar), KeyMap.Combination(HotkeyAction.QuickBar, settings)),
        new(TranslateClipboardKey, KeyMap.TrayLabel(HotkeyAction.ClipboardTranslate), KeyMap.Combination(HotkeyAction.ClipboardTranslate, settings)),
        new(ReverseInputKey, KeyMap.TrayLabel(HotkeyAction.ReverseInput), KeyMap.Combination(HotkeyAction.ReverseInput, settings)),
        new(string.Empty, null, null),
        new(LibraryKey, KeyMap.TrayLabel(HotkeyAction.Library), KeyMap.Combination(HotkeyAction.Library, settings)),
        new(SettingsKey, "设置…", null),
        new(KeymapKey, "键位速查…", null),
        new(string.Empty, null, null),
        .. PauseRows(paused),
        new(string.Empty, null, null),
        new(UpdateKey, "检查更新…", null),
        new(QuitKey, "退出拾语", null),
    ];

    private static IReadOnlyList<TrayMenuRow> PauseRows(bool paused)
        => paused
            ? [new(ResumeKey, "恢复记录", null)]
            :
            [
                new(Pause15Key, "暂停记录 15 分钟", null),
                new(Pause60Key, "暂停记录 1 小时", null),
                new(PauseKey, "暂停记录，直到恢复", null),
            ];

    private static void Run(AppShell shell, string key)
    {
        switch (key)
        {
            case QuickPasteKey:
                // 同反向输入：窄条要记下"贴回哪扇窗"，菜单收起的那一拍里前台还是拾语的
                // 消息窗口。托盘成了窄条唯一的手动入口（用户需求 2026-10-10），这一拍不能省。
                RunAfterMenuSettles(() => shell.ShowQuickPaste?.Invoke());
                break;
            case TranslateClipboardKey:
                shell.TranslateClipboard?.Invoke();
                break;
            case ReverseInputKey:
                // 托盘菜单刚收起，前台还在回到用户原来所在应用的路上：等一拍再呼出，否则框记下的
                // "原来的窗口"会是拾语自己的消息窗口，回贴就没有地方可回。
                RunAfterMenuSettles(() => shell.ShowReverseInput?.Invoke());
                break;
            case LibraryKey:
                shell.ShowLibrary?.Invoke();
                break;
            case SettingsKey:
                shell.ShowSettings?.Invoke();
                break;
            case KeymapKey:
                // 深链进快捷键页的速查区：托盘问"键怎么按"，直接落到答案上。
                shell.OpenSettingsAt?.Invoke("hotkeys.cheatsheet");
                break;
            case Pause15Key:
                Pause(shell, TimeSpan.FromMinutes(15));
                break;
            case Pause60Key:
                Pause(shell, TimeSpan.FromHours(1));
                break;
            case PauseKey:
                Pause(shell, duration: null);
                break;
            case ResumeKey:
                shell.RecordingPause.Resume();
                shell.TellUser("已恢复记录剪贴板。");
                break;
            case UpdateKey:
                shell.ShowUpdateWindow?.Invoke();
                break;
            case QuitKey:
                Application.Current.Shutdown();
                break;
        }
    }

    /// <summary>暂停记录，并把"到几点、暂停时不做什么"说清楚。</summary>
    private static void Pause(AppShell shell, TimeSpan? duration)
    {
        var now = DateTimeOffset.Now;
        if (duration is { } span)
        {
            shell.RecordingPause.PauseFor(span, now);
        }
        else
        {
            shell.RecordingPause.PauseUntilResumed();
        }

        shell.TellUser($"{shell.RecordingPause.Describe(now)}。这期间复制的内容不进历史，也不弹徽标。");
    }

    /// <summary>托盘照暂停现状换图标与提示文字，定时暂停排好到点恢复的闹钟。</summary>
    private void ShowPauseState(TrayIcon tray, RecordingPause pause)
    {
        var now = DateTimeOffset.Now;
        var described = pause.Describe(now);
        tray.ShowPaused(described is not null, described is null ? "拾语" : "拾语 · " + described);

        _resume?.Stop();
        if (pause.Until is { } until && until > now)
        {
            _resume ??= NewResumeTimer(tray, pause);
            _resume.Interval = until - now;
            _resume.Start();
        }
    }

    private static DispatcherTimer NewResumeTimer(TrayIcon tray, RecordingPause pause)
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (pause.Until is { } until && DateTimeOffset.Now >= until)
            {
                pause.Resume();
                tray.ShowNotification("拾语", "已恢复记录剪贴板。");
            }
        };
        return timer;
    }

    /// <summary>单发地等一拍再执行（托盘菜单收起后，前台回到原应用需要一两帧）。</summary>
    private static void RunAfterMenuSettles(Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
