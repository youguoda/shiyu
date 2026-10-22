using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// The updater's face (ticket 28): what is installed, what is newest, what
/// changed, how far the download has come — and a plain sentence when
/// something goes wrong, instead of a broken half-install.
/// </summary>
internal sealed class UpdateWindow : Window
{
    private readonly UpdateService _updates;

    private readonly TextBlock _currentLabel = new();

    private readonly TextBlock _latestLabel = new();

    private readonly TextBox _notes = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        Height = 180,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private readonly ProgressBar _progress = new()
    {
        Height = 6,
        Minimum = 0,
        Maximum = 1,
        Margin = new Thickness(0, 10, 0, 0),
    };

    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Button _check = new() { Content = "检查更新", Padding = new Thickness(14, 5, 14, 5), Cursor = System.Windows.Input.Cursors.Hand };

    private readonly Button _install = new()
    {
        Content = "下载并安装",
        Padding = new Thickness(14, 5, 14, 5),
        Margin = new Thickness(6, 0, 0, 0),
        Cursor = System.Windows.Input.Cursors.Hand,
        IsEnabled = false,
    };

    private ReleaseManifest? _release;

    /// <summary>
    /// 关窗即作罢：下载停下、半成品放手。不停的话，再开一扇更新窗就是两个下载抢同一个
    /// 暂存区（用户日志 2026-10-19），而且下完还会在用户已经关掉窗口之后重启拾语。
    /// </summary>
    private readonly CancellationTokenSource _closing = new();

    public UpdateWindow(UpdateService updates)
    {
        _updates = updates;
        Closed += (_, _) => _closing.Cancel();

        Title = "拾语 · 更新";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        MinWidth = 380;
        Background = (Brush)FindResource("Brush.Background");
        FontSize = (double)FindResource("Type.Body");

        // R1（票 18）：隐式 TextBlock 样式已删，字体与文字色由窗口根继承。
        SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");

        _currentLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _latestLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        _currentLabel.Text = $"当前版本 v{UpdateService.CurrentDisplay}";
        _latestLabel.Text = "最新版本 未知";

        _notes.SetResourceReference(TextBox.BackgroundProperty, "Brush.SurfaceInput");
        _notes.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text");
        // 更新说明（release notes）是被阅读的正文：Type.Content 18。
        _notes.FontSize = (double)FindResource("Type.Content");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _check.Click += async (_, _) => await GuardedAsync(CheckAsync);
        _install.Click += async (_, _) => await GuardedAsync(InstallAsync);
        buttons.Children.Add(_check);
        buttons.Children.Add(_install);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(_currentLabel);
        panel.Children.Add(_latestLabel);
        panel.Children.Add(_notes);
        panel.Children.Add(_progress);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += async (_, _) => await GuardedAsync(CheckAsync);
    }

    /// <summary>
    /// 点击处理里的任何异常都收敛成一句话（票 09）：一个下载路径上的意外
    /// 不该带走整个进程——常驻应用崩一次，比一次失败的更新贵得多。
    /// </summary>
    private async Task GuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.UpdateApplyFailed, failure, ("window", 1));
            _progress.Value = 0;
            _status.Text = "操作失败：" + failure.Message;
            _check.IsEnabled = true;
        }
    }

    private async Task CheckAsync()
    {
        _check.IsEnabled = false;
        _install.IsEnabled = false;
        _status.Text = "正在检查…";

        try
        {
            var check = await _updates.CheckAsync(_closing.Token);
            _release = check.Release;

            if (_release is null)
            {
                Log.Event(LogEvent.UpdateCheckFailed, [.. check.LogFields, ("window", 1)]);
                _status.Text = check.Problem;
            }
            else if (!_release.IsNewerThan(UpdateService.Current))
            {
                _latestLabel.Text = $"最新版本 v{_release.Version.Text}";
                _status.Text = "已是最新版本。";
            }
            else
            {
                _latestLabel.Text = $"最新版本 v{_release.Version.Text}";
                _notes.Text = _release.Notes;
                _status.Text = "有新版本可用。";
                _install.IsEnabled = true;
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // 窗口关了，检查随之作罢：不是故障，不进日志。
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.UpdateCheckFailed, failure, ("window", 1));
            _status.Text = "检查失败：" + failure.Message;
        }
        finally
        {
            _check.IsEnabled = true;
        }
    }

    private async Task InstallAsync()
    {
        if (_release is null)
        {
            return;
        }

        _install.IsEnabled = false;
        _check.IsEnabled = false;
        _status.Text = "正在下载…";
        _progress.Value = 0;

        var progress = new Progress<double>(fraction => _progress.Value = Math.Clamp(fraction, 0, 1));

        var (ok, error) = await _updates.DownloadAsync(_release, progress, _closing.Token);

        if (!ok)
        {
            _progress.Value = 0;
            _status.Text = error + " 原版本不受影响。";
            _check.IsEnabled = true;
            return;
        }

        _status.Text = "已就绪，重启完成更新。";

        // A beat so the sentence can be read, then the staged copy takes over —
        // unless the window was closed in that beat: closing means "not now".
        var beat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        beat.Tick += (_, _) =>
        {
            beat.Stop();
            if (!_closing.IsCancellationRequested)
            {
                _updates.ApplyAndRestart();
            }
        };
        beat.Start();
    }
}
