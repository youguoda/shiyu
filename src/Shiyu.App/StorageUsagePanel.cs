using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// What Shiyu actually takes from the disk, measured where it lies —
/// the number a user asking "这个小工具吃了我多少" is owed, with the
/// folder one click away and a plain word when it grows past reason.
/// </summary>
internal static class StorageUsagePanel
{
    public static FrameworkElement Build()
    {
        var panel = new StackPanel();

        TextBlock Row(string name, long bytes)
        {
            var row = new TextBlock
            {
                Text = $"{name}  {FormatBytes(bytes)}",
                Margin = new Thickness(0, 2, 0, 2),
            };
            row.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            row.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            return row;
        }

        void Measure()
        {
            panel.Children.Clear();

            var database = SizeOfFile(AppPaths.DatabaseFile)
                + SizeOfFile(AppPaths.DatabaseFile + "-wal")
                + SizeOfFile(AppPaths.DatabaseFile + "-shm");
            var images = SizeOfDirectory(AppPaths.ImageDirectory);

            panel.Children.Add(Row("数据库", database));
            panel.Children.Add(Row("图片原图", images));
            panel.Children.Add(Row("合计", database + images));

            const long threshold = 500L * 1024 * 1024;
            if (database + images > threshold)
            {
                var warning = new TextBlock
                {
                    Text = "已超过 500 MB——考虑缩短图片保留天数，或导出备份后清空。",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                };
                warning.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
                warning.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
                warning.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
                panel.Children.Add(warning);
            }
        }

        Measure();

        var open = new Button
        {
            Content = "打开数据文件夹",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand,
        };
        open.Click += (_, _) =>
        {
            try
            {
                using var opened = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(AppPaths.DataDirectory)
                    {
                        UseShellExecute = true,
                    });
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.OpenLinkFailed, failure, ("folder", 1));
                ((App)Application.Current).TellUser("没能打开数据文件夹。");
            }
        };

        var refresh = new Button
        {
            Content = "刷新",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 6, 0, 0),
            Cursor = Cursors.Hand,
        };
        refresh.Click += (_, _) => Measure();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(open);
        row.Children.Add(refresh);
        panel.Children.Add(row);

        return panel;
    }

    private static long SizeOfFile(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long SizeOfDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
            : $"{bytes / 1024.0:0.#} KB";
}
