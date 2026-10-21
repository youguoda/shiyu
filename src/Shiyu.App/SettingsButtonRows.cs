using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 设置窗里只摆一两个按钮的行：关于页（品牌头、检查更新、日志、重新运行引导、隐私说明）与数据页的备份。
/// 都不读写设置项本身，只把点击交给对应的去处。
/// </summary>
internal static class SettingsButtonRows
{
    /// <summary>
    /// 品牌头（§5.1 关于页）：卡标签即名称、说明即一句话，控件列放标识
    /// 与「复制版本信息」——不多占一行去重复一遍"拾语"。
    /// </summary>
    public static FrameworkElement BrandHeader()
    {
        // 与任务栏图标同一个标志（大号母版，矢量）。
        var mark = new Image { Width = 40, Height = 40 };
        mark.SetResourceReference(Image.SourceProperty, "Brand.Mark.Large");

        var copy = new Button
        {
            Content = "复制版本信息",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "版本号与运行环境，不含任何个人数据",
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(
                    $"拾语 {SettingsBindings.VersionText} / {Environment.OSVersion.VersionString}");
                ((App)Application.Current).TellUser("版本信息已复制。");
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.OpenLinkFailed, failure, ("clipboard", 1));
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(mark);
        row.Children.Add(copy);
        return row;
    }

    public static FrameworkElement UpdateCheck(Window owner, Action<Window>? checkForUpdate)
    {
        var check = new Button
        {
            Content = "检查更新",
            Padding = new Thickness(12, 3, 12, 3),
            Cursor = Cursors.Hand,
        };
        check.Click += (_, _) => checkForUpdate?.Invoke(owner);
        return check;
    }

    public static FrameworkElement Logs()
    {
        var open = new Button
        {
            Content = "打开日志目录",
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        open.Click += (_, _) =>
        {
            try
            {
                using var opened = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Path.Combine(AppPaths.DataDirectory, "logs"))
                    {
                        UseShellExecute = true,
                    });
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.OpenLinkFailed, failure, ("folder", 2));
                ((App)Application.Current).TellUser("没能打开日志目录。");
            }
        };
        return open;
    }

    /// <param name="shell">点下时才取：设置窗建页时壳还没接上。</param>
    public static FrameworkElement Onboarding(Window owner, SettingsStore store, Func<AppShell?> shell)
    {
        var run = new Button
        {
            Content = "重新运行引导",
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        run.Click += (_, _) =>
        {
            // 重跑引导（§5.3）：不是首启——译文语言尊重已存值，试一试的
            // 实时检测经壳接上（shell 为空时引导自己说明退化）。
            var wizard = new OnboardingWindow(store, firstRun: false, shell())
            {
                Owner = owner,
            };
            wizard.ShowDialog();
        };
        return run;
    }

    /// <summary>隐私说明的正文就是卡片的说明列（schema Hint）；控件列为空。</summary>
    public static FrameworkElement PrivacyNote()
        => new TextBlock();

    public static FrameworkElement Backup(Window owner, BackupUi? backup)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var export = new Button { Content = "导出备份…", MinWidth = 96, Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand };
        export.Click += (_, _) => backup?.Export(owner);

        var import = new Button { Content = "导入备份…", MinWidth = 96, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
        import.Click += (_, _) => backup?.Import(owner);

        row.Children.Add(export);
        row.Children.Add(import);
        return row;
    }
}
