using Shiyu.Core;

namespace Shiyu.App;

/// <summary>更新模块（O-40 拆自 App.xaml.cs）：更新窗单例与启动后的一次安静检查。</summary>
internal sealed class UpdateModule
{
    private AppShell? _shell;
    private UpdateWindow? _window;

    public void Attach(AppShell shell) => _shell = shell;

    /// <summary>
    /// The manual entrance to the updater: one window at a time, from the tray
    /// menu or the startup probe — a second click activates the first one
    /// instead of stacking a racing download beside it.
    /// </summary>
    public void ShowWindow()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        _window = new UpdateWindow(new UpdateService(AppPaths.DataDirectory));
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    /// <summary>
    /// One quiet check, shortly after startup, when the setting allows it.
    /// Finding something new raises a tray notification and nothing else —
    /// installing is the user's click, never ours. The listener and
    /// everything else keeps running throughout; the check touches only the
    /// network and the staging directory.
    /// </summary>
    public void StartWatch(AppShell shell)
    {
        _shell = shell;

        if (!shell.Settings.UpdateAutoCheck)
        {
            return;
        }

        var deferred = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };

        deferred.Tick += async (_, _) =>
        {
            deferred.Stop();

            try
            {
                var check = await new UpdateService(AppPaths.DataDirectory).CheckAsync();
                if (check.Release is not { } release)
                {
                    // 安静检查不打扰用户，但「已是最新」与「没查到」不能在日志里记成同一行。
                    Log.Event(LogEvent.UpdateCheckFailed, check.LogFields);
                }
                else if (release.IsNewerThan(UpdateService.Current))
                {
                    Log.Event(LogEvent.UpdateChecked, ("found", true));
                    shell.Notify(
                        "拾语有新版本",
                        $"v{release.Version.Text} 已发布。右键托盘图标 → 检查更新 安装。");
                }
                else
                {
                    Log.Event(LogEvent.UpdateChecked, ("found", false));
                }
            }
            catch (Exception failure)
            {
                // A quiet check that cannot reach the channel says nothing to
                // the user — 404 或断网没有可点的动作——但 O-24 要求留下
                // 一行日志，别让通道坏了只能靠猜。
                Log.Event(LogEvent.UpdateCheckFailed, failure);
            }
        };

        deferred.Start();
    }

    /// <summary>原 OnExit：更新窗与设置窗同一批关掉。</summary>
    public void Shutdown() => _window?.Close();
}
