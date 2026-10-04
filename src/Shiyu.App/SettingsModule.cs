using Shiyu.Core;

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
            _window.Activate();
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
            restored = restored.KeepingKeyOf(_shell!.Settings);

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
    /// Opens the settings window landed on one item — the deep link other
    /// windows use instead of knowing anything about settings internals.
    /// </summary>
    public void ShowAt(string itemId)
    {
        Show();
        _window?.JumpToItem(itemId);
    }

    /// <summary>原 OnExit：设置窗先于几何保存关掉。</summary>
    public void Shutdown() => _window?.Close();
}
