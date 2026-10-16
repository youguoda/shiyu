using System.Windows;
using System.Windows.Interop;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 设置窗与备份模块（O-40 拆自 App.xaml.cs）：一扇设置窗、深链定位、
/// 备份导入的设置落地——一切写经 store，生效走同一条变更广播。
/// </summary>
internal sealed class SettingsModule
{
    private AppShell? _shell;
    private SettingsWindow? _window;

    public void Attach(AppShell shell) => _shell = shell;

    /// <summary>
    /// Opens the settings window. It reads from the store and submits only
    /// what the user changed; live effects come from the same SettingsChanged
    /// everyone else answers to.
    /// </summary>
    public void Show()
    {
        var shell = _shell!;

        if (_window is not null)
        {
            BringToFront(_window);
            return;
        }

        _window = new SettingsWindow(
            shell.SettingsStore,
            new BackupUi(
                shell.Store,
                AppPaths.ImageDirectory,
                includeKey => shell.Settings.ToBackupJson(includeKey),
                RestoreSettingsFromBackup),

            // 关于页的「检查更新」（§5.1）：与托盘菜单同一个手动入口；
            // 「重新运行引导」的试一试信号经壳进来（票 25）。
            _ => shell.ShowUpdateWindow?.Invoke())
        {
            Shell = shell,
        };
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        BringToFront(_window);

        /// <summary>
        /// A backup's settings land the way every write does: through the
        /// store, whole-record, then applied live by the one changed handler.
        /// The settings window refreshes itself from that same event, so a
        /// later save can no longer roll the import back from a stale
        /// snapshot (S3).
        ///
        /// Called on the UI thread by BackupUi once the import has landed.
        /// Answers null when applied, or the plain-words reason the current
        /// settings were kept: an unparseable backup must not be written to
        /// disk, where a later load would quietly reset the user to defaults.
        /// </summary>
        string? RestoreSettingsFromBackup(string json)
        {
            if (!AppSettings.TryParse(json, out var restored))
            {
                return "条目已导入，但备份里的设置无法识别，已保留当前设置。";
            }

            // A backup made without the key (the default, ADR-0011) must not
            // wipe the key this machine already has: the user asked to import
            // history, not to log out of their translation service. The key
            // stays together with its origin (票 29): the backup may point at
            // another service, and then the old key simply goes unused.
            restored = restored.KeepingKeyOf(_shell!.Settings).KeepingAutoBackupOf(_shell!.Settings);

            try
            {
                _shell!.SettingsStore.Update(_ => restored, AppPaths.SettingsFile);
            }
            catch (SettingsSaveException)
            {
                // The store refused the change: memory and disk still hold the
                // pre-import settings, so that is exactly what the
                // summary should claim.
                return "条目已导入，但设置没能写入磁盘，已保留当前设置。";
            }

            return null;
        }
    }

    /// <summary>
    /// 设置窗摆到用户眼前（用户实录 2026-10-13：点托盘图标，设置窗有时不在最前）。只调
    /// Activate 不够：已经开着的窗口最小化时不会还原；托盘的那一下点击落在任务栏上，激活又常被
    /// Windows 的前台锁挡下，只闪一下任务栏按钮。这里最小化的先还原，挡下了就走与反向输入框
    /// 回贴同一条兜底序列。托盘、再次启动拾语、别处的深链都从这里进。
    /// </summary>
    private static void BringToFront(Window window)
        => new ForegroundWindow(new WindowInteropHelper(window).Handle).BringToFront();

    /// <summary>
    /// Opens the settings window landed on one item — the deep link other
    /// windows use instead of knowing anything about settings internals.
    /// </summary>
    public void ShowAt(string itemId)
    {
        Show();
        _window?.JumpToItem(itemId);
    }

#if DEBUG
    /// <summary>
    /// 探针命令 credentials 的入口（见 SettingsWindow.ProbeCredentials）：设置里放好两家——DeepSeek
    /// （用的是备选模型）与智谱，当前在智谱上——再开设置窗落到凭据。最后核对落盘的文件里没有明文。
    /// </summary>
    internal async void ProbeCredentials()
    {
        var shell = _shell!;
        shell.TryUpdateSettings(settings =>
            ((settings with
                {
                    SavedProviders = [],
                    TranslationBackend = TranslationBackendKind.OwnKey,
                    BackendPresetId = "deepseek",
                    BackendBaseUrl = "https://api.deepseek.com",
                    BackendModel = "deepseek-v4-pro",
                }).WithApiKey("sk-deepseek-probe-1234567890")
                with
                {
                    BackendPresetId = "zhipu",
                    BackendBaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                    BackendModel = "glm-4-flash-250414",
                })
            .WithApiKey("sk-zhipu-probe-0987654321"));

        ShowAt("service.api-key");
        for (var waited = 0; waited < 50 && _window is not { IsLoaded: true }; waited++)
        {
            await Task.Delay(100);
        }

        var directory = DebugOverrides.ProbeDirectory!;
        var log = _window is null
            ? "window|missing" + Environment.NewLine
            : await _window.ProbeCredentials(System.IO.Path.Combine(directory, "credentials.png"));

        var stored = System.IO.File.ReadAllText(AppPaths.SettingsFile);
        log += FormattableString.Invariant(
            $"disk|plain={stored.Contains("-probe-", StringComparison.Ordinal)}|protected={stored.Contains(AppSettings.SecretMarker, StringComparison.Ordinal)}{Environment.NewLine}");
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "credentials.log"), log + "done" + Environment.NewLine);
    }

    /// <summary>探针命令 settings-more 的入口（评审 2026-10-16 F-15，见 SettingsWindow.ProbeMore）。</summary>
    internal async void ProbeMore()
    {
        Show();
        for (var waited = 0; waited < 50 && _window is not { IsLoaded: true }; waited++)
        {
            await Task.Delay(100);
        }

        var directory = DebugOverrides.ProbeDirectory!;
        var log = _window is null
            ? "window|missing" + Environment.NewLine
            : await _window.ProbeMore(
                System.IO.Path.Combine(directory, "settings-more-closed.png"),
                System.IO.Path.Combine(directory, "settings-more-open.png"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "settings-more.log"), log + "done" + Environment.NewLine);
    }

    /// <summary>
    /// 探针命令 settings-front（用户实录 2026-10-13：点托盘图标，设置窗有时不在最前）。托盘左键走的
    /// 就是 <see cref="Show"/>：开一次；最小化后再开；最大化再最小化后再开（应回到最大化）；被本进程的
    /// 另一扇窗盖住后再开。每一步记窗口状态、是不是前台。托盘点击后本进程没有资格抢前台的那种前台锁，
    /// 探针造不出来——那一步的时序在 Core 的 ForegroundReclaim 测试里钉着。
    /// </summary>
    internal async void ProbeBringToFront()
    {
        var log = new System.Text.StringBuilder();

        Show();
        await Settle();
        Line("open");

        _window!.WindowState = WindowState.Minimized;
        await Settle();
        Show();
        await Settle();
        Line("restored");

        _window.WindowState = WindowState.Maximized;
        await Settle();
        _window.WindowState = WindowState.Minimized;
        await Settle();
        Show();
        await Settle();
        Line("maximized");

        _window.WindowState = WindowState.Normal;
        var cover = new Window
        {
            Title = "probe cover",
            Left = _window.Left,
            Top = _window.Top,
            Width = _window.ActualWidth,
            Height = _window.ActualHeight,
            ShowInTaskbar = false,
        };
        cover.Show();
        cover.Activate();
        await Settle();
        var coverInFront = new ForegroundWindow(new WindowInteropHelper(cover).Handle).IsForeground;
        Show();
        await Settle();
        log.AppendLine(FormattableString.Invariant($"cover|inFront={coverInFront}"));
        Line("uncovered");
        cover.Close();

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(DebugOverrides.ProbeDirectory!, "settings-front.log"), log + "done" + Environment.NewLine);

        static async Task Settle()
        {
            await Task.Delay(400);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        void Line(string step)
        {
            var handle = new WindowInteropHelper(_window!).Handle;
            log.AppendLine(FormattableString.Invariant(
                $"{step}|state={_window!.WindowState}|foreground={new ForegroundWindow(handle).IsForeground}"));
        }
    }
#endif

    /// <summary>原 OnExit：设置窗先于几何保存关掉。</summary>
    public void Shutdown() => _window?.Close();
}
