using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 定期备份（评审 2026-10-16 F-20）的「备份到」三行：文件夹、口令、上次备份与「立即备份一次」。写设置走
/// store（整份记录）；口令从问到保护都是字节，存下的只是受保护的形式。状态在上屏、自己改了什么、后台备份
/// 跑完时重读文件夹。
/// </summary>
internal static class AutoBackupRow
{
    /// <param name="shell">设置窗建页时壳还没接上，上屏与点击时才取。</param>
    /// <param name="showError">把话说在这一行所在的设置卡上。</param>
    public static FrameworkElement Build(
        Window owner, SettingsStore store, Func<AppShell?> shell, Action<string> showError)
    {
        TextBlock Caption()
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            text.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            return text;
        }

        Button Action(string label)
            => new() { Content = label, Padding = new Thickness(10, 3, 10, 3), Cursor = Cursors.Hand };

        DockPanel Line(TextBlock text, Button button)
        {
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(button, Dock.Right);
            line.Children.Add(button);
            line.Children.Add(text);
            return line;
        }

        var folderText = Caption();
        var pick = Action("选择文件夹…");
        var lockText = Caption();
        var setPassword = Action("设置口令…");
        var statusText = Caption();
        var runNow = Action("立即备份一次");

        var panel = new StackPanel();
        panel.Children.Add(Line(folderText, pick));
        panel.Children.Add(Line(lockText, setPassword));
        panel.Children.Add(Line(statusText, runNow));

        // 「立即备份一次」的结果留在状态行前面，直到换了文件夹或口令；后台备份跑完的刷新不会把它冲掉。
        var running = false;
        string? outcome = null;
        void Refresh()
        {
            var settings = store.Current;
            var folder = settings.AutoBackupFolder.Trim();
            folderText.Text = folder.Length > 0 ? folder : "还没选文件夹。";
            lockText.Text = settings.AutoBackupPassword.Length > 0
                ? "已加密：口令保存在这台电脑上，换电脑恢复时要用它。"
                : "不加密：拿到备份文件的人都能打开。";
            statusText.Text = running
                ? "正在备份…"
                : (outcome is { Length: > 0 } said ? said + " " : string.Empty) + AutoBackupModule.Describe(settings);
            runNow.IsEnabled = folder.Length > 0 && !running;
        }

        void Save(Func<AppSettings, AppSettings> change)
        {
            try
            {
                store.Update(change, AppPaths.SettingsFile);
            }
            catch (SettingsSaveException failure)
            {
                showError(failure.Message + " 可以重试。");
            }

            outcome = null;
            Refresh();
        }

        pick.Click += (_, _) =>
        {
            var current = store.Current.AutoBackupFolder;
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择定期备份存放的文件夹",
                InitialDirectory = Directory.Exists(current)
                    ? current
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(owner) == true)
            {
                var chosen = dialog.FolderName;
                Save(latest => latest with { AutoBackupFolder = chosen });
            }
        };

        setPassword.Click += (_, _) =>
        {
            var (cancelled, bytes) = BackupUi.AskAutoBackupPassword(owner);
            if (cancelled)
            {
                return;
            }

            if (bytes is null)
            {
                Save(latest => latest with { AutoBackupPassword = string.Empty });
                return;
            }

            string? sealedPassword;
            try
            {
                sealedPassword = BackupPasswordVault.Protect(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }

            if (sealedPassword is null)
            {
                showError("口令没能在这台电脑上保护起来，没有保存。可以重试。");
                return;
            }

            Save(latest => latest with { AutoBackupPassword = sealedPassword });
        };

        runNow.Click += async (_, _) =>
        {
            if (shell()?.AutoBackup is not { } module)
            {
                return;
            }

            // async void 逃出去的异常是进程级崩溃（O-05）：RunAsync 自己收住备份的失败，这里兜界面的意外。
            running = true;
            Refresh();
            try
            {
                outcome = await module.RunAsync(manual: true);
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.AutoBackupFailed, failure, ("manual", true));
                outcome = "没能备份：出了意外，详情在日志里。";
            }
            finally
            {
                running = false;
                Refresh();
            }
        };

        // 后台的定时备份跑完，开着的设置窗跟着更新；上屏才订、下屏退订（同翻译记录那一行）。
        void OnFinished() => panel.Dispatcher.BeginInvoke(Refresh);
        AutoBackupModule? watched = null;
        panel.Loaded += (_, _) =>
        {
            if (watched is null && shell()?.AutoBackup is { } module)
            {
                watched = module;
                module.Finished += OnFinished;
            }

            Refresh();
        };
        panel.Unloaded += (_, _) =>
        {
            if (watched is not null)
            {
                watched.Finished -= OnFinished;
                watched = null;
            }
        };

        return panel;
    }
}
