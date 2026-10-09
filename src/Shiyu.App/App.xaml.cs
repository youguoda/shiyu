using System.Windows;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 生命周期宿主（O-40）：OnStartup 的装配、OnExit 的反向拆除、单实例、
/// 全局兜底的安装。一切功能住在功能模块里（翻译、取词、窄条、管理窗、
/// 设置与备份、更新、托盘、保留清理），经 <see cref="AppShell"/> 通信，
/// 设置生效走各模块自己的 Apply（票 07 的单一应用函数由此拆散）。
/// </summary>
public partial class App : Application
{
    private AppShell? _shell;
    private AppDiagnostics? _diagnostics;
    private AppModules? _modules;
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The staged copy of an update runs with this argument: it swaps the
        // install and restarts the real application, showing nothing. Before
        // anything else — a finalizer has no business owning a clipboard
        // listener or a tray icon for the seconds it lives. Environment.Exit
        // rather than Shutdown: OnExit tears down windows this path never
        // built, and by then the staging reset has removed the staged copy's
        // own DLLs, so even JITting that teardown method would crash.
        if (UpdateService.TryRunFinalizer(e.Args))
        {
            Environment.Exit(0);
        }

        // 先于一切业务接线（O-05）：处理器挂上之后，启动路径上的任何闪失
        // 才有日志与托盘兜底，而不是把进程直接带走——对一个托盘常驻的
        // 记录工具，崩溃就等于静默停止记录。
        _diagnostics = new AppDiagnostics();
        _diagnostics.Install();

        try
        {
            Start();
        }
        catch (Exception exception)
        {
            // Shiyu has no window. Without this, a failure to start is a
            // process that silently isn't there — nothing to look at, nothing
            // to read. The file is the only way in.
            Log.Event(LogEvent.StartupFailed, exception);
            AppDiagnostics.RecordStartupFailure(exception);
            throw;
        }
    }

    private void Start()
    {
        // The probe decision precedes the mutex: the name is the whole trick
        // that lets a probe instance run beside the user's real one (票 15).
        // Same data directory => same mutex, so probes of one directory still
        // reject each other; a different directory never collides with "Shiyu".
        var mutexName = "Shiyu";
#if DEBUG
        mutexName = PrepareProbe();
#endif

        _singleInstance = SingleInstance.Acquire(mutexName);
        if (!_singleInstance.IsOnlyInstance)
        {
            // Before anything else touches the clipboard or the history: a
            // rejected second instance must leave no trace behind it.
            _singleInstance.NotifyExistingInstance();
            Shutdown();
            return;
        }

        var (settingsStore, quarantineNotice) = AppShell.LoadSettings();

        // 数据目录定下来这刻起，一切后续失败都有处可写（O-05）。同步 sink：
        // 致命异常的最后一行必须在进程倒下之前落盘。
        Log.Attach(new FileLogSink(AppPaths.DataDirectory));

#if DEBUG
        // 实机验收用的隐藏命令（票 06）：仅调试构建存在。
        InstallDebugTickCrash();
#endif

        // The relay's device identity（详见 AppShell.EnsureRelayClientId 的
        // 说明）：设备匿名 id，生成一次、机器终生稳定——壳建好后、Connect
        // 前落账（此时订阅尚未接线，启动期这笔写不触发变更广播）。
        var needsRelayId = settingsStore.Current.RelayClientId.Length == 0;

        // The finalizer's unfinished chore: it cannot delete the staged
        // directory it was running from. By now it has exited.
        UpdateStaging.CleanStagedIfIdle(AppPaths.DataDirectory);

        var store = EntryStore.Open(AppPaths.DatabaseFile);

        // One hidden window serves the clipboard notifications, the tray
        // icon's callbacks, and the global hotkeys — and carries the
        // WM_SETTINGCHANGE broadcasts the theme follows (O-37). Built before
        // the theme manager so the watcher can ride it from birth: the
        // first frame a window ever shows must already be in the right
        // theme, and a theme flip in that first instant is news all the
        // same.
        var messageWindow = new MessageWindow();
        var theme = new ThemeManager(messageWindow);
        theme.Apply(settingsStore.Current.Theme);
        theme.ApplyContentSize(settingsStore.Current.ContentFontSize);

        var shell = new AppShell
        {
            SettingsStore = settingsStore,
            MessageWindow = messageWindow,
            Store = store,
            Icons = new AppIconCache(store),
            FileIcons = new FileTypeIcons(),

            // Every renderer reads its verdicts from this one cache (O-36):
            // the bar's cards, the preview panel — a path probed for one is a
            // path the other never waits for.
            FileProbe = new FileExistenceCache(),
            Images = new ImageArchive(AppPaths.ImageDirectory),
            Theme = theme,
            Exclusions = settingsStore.Current.BuildExclusionPolicy(),
            IsProbe = DebugOverrides.IsProbe,
        };
        _shell = shell;
        _diagnostics!.Tell = shell.TellUser;

        if (needsRelayId)
        {
            shell.EnsureRelayClientId();
        }

        shell.AttachClipboardPipeline();

        // 托盘先于热键与钩子：它们失败时要说的话，得有地方说。
        new TrayModule().Attach(shell, quarantineNotice);

        shell.AttachCapture();
        _modules = AppModules.Attach(shell);

        // The probe keeps out of the real instance's wake-up channel: the
        // "another instance started" broadcast goes to every Shiyu process,
        // and a probe answering it would throw a settings window at the user.
        if (!shell.IsProbe)
        {
            _singleInstance.WatchForOtherInstances(messageWindow);

            // Starting Shiyu again — a pinned taskbar icon, the Start menu —
            // is how a user asks to see it, so bring settings up rather than
            // only saying "already running" and leaving them no further
            // along. Same destination as a click on the tray icon (用户需求
            // 2026-10-05: clicking the app's icon opens settings).
            _singleInstance.AnotherInstanceStarted += () => shell.ShowSettings?.Invoke();
        }

        _modules.Retention.Start(shell);
        StartupPreference.Apply(shell.Settings);
        if (!shell.IsProbe)
        {
            _modules.Update.StartWatch(shell);
        }

#if DEBUG
        // 调试探针的启动便利（O-41）：Release 构建里整个文件为空。
        OpenDebugStartupWindows();
#endif

        // The first-run guide (§5.3 five screens, ticket 25) asks the few
        // things only the user knows, then walks them through really using
        // the bar once. A history that already exists says this is not a
        // first run — the guide stays away and never nags an upgrading user.
        // A probe skips it on principle: the whole point is a deterministic,
        // empty surface. Every step commits on leave through the store, so
        // what it collects applies live via the same SettingsChanged path a
        // settings save takes, and a bar summoned mid-guide can no longer
        // erase it (S2).
        if (!shell.IsProbe && !shell.Settings.OnboardingCompleted && store.Count() == 0)
        {
            new OnboardingWindow(settingsStore, firstRun: true, shell).Show();
        }

#if DEBUG
        RunProbeCommand();
#endif
    }

    /// <summary>子窗口经 App 拿到的一句话通道（转发 shell）。</summary>
    internal void TellUser(string message) => _shell?.TellUser(message);

    /// <summary>设置深链的对外入口（转发设置模块）。</summary>
    internal void OpenSettingsAt(string itemId) => _modules?.Settings.ShowAt(itemId);

    protected override void OnExit(ExitEventArgs e)
    {
        // 第一件事就是还债（O-05 修正）：划词借走的剪贴板必须在任何可能
        // 抛出的清理之前归还——原次序里它排在几何保存之后，Save 一抛，
        // 用户就带着我们借走的内容走了。账本把在册的债一次结清（新债
        // 先还、旧债压轴，最终留在剪贴板里的是用户取词前的原文）。
        _modules?.Selection.SettleOnExit();

        // Reverse order of construction: the tray and the clipboard listener
        // both hold the message window.
        _modules?.Retention.Shutdown();
        _shell?.Theme.Dispose();
        _modules?.Settings.Shutdown();
        _modules?.Update.Shutdown();
        _modules?.Bar.Shutdown();
        _modules?.ReverseInput.Shutdown();
        _modules?.Translation.Shutdown();
        _modules?.Selection.Shutdown();
        _shell?.Dispose();
        _modules?.Hotkeys.Shutdown();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
