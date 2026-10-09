using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 保留清理模块（O-40 拆自 App.xaml.cs）：过期图片原件与到期翻译记录的即时与每日清扫。
/// </summary>
internal sealed class RetentionModule
{
    private AppShell? _shell;
    private System.Windows.Threading.DispatcherTimer? _timer;

    /// <summary>上一次清扫翻译记录时的周期：周期一变就再扫一次，不等明天。</summary>
    private int _logDays;

    /// <summary>
    /// Sweeps expired image originals and translation records now and once a
    /// day thereafter.
    ///
    /// Run on a background thread: a machine left unused for months has a
    /// backlog to work through, and doing it on the thread that draws would
    /// make startup look like a hang.
    /// </summary>
    public void Start(AppShell shell)
    {
        _shell = shell;

        Sweep();
        SweepTranslationLog();

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(24),
        };
        _timer.Tick += (_, _) =>
        {
            Sweep();
            SweepTranslationLog();
        };
        _timer.Start();

        // 翻译记录的周期改了即刻作数（用户需求 2026-10-09）：从 30 天改成 7 天，8 天前的
        // 记录此刻就清，而不是等到明天的这个时候。
        shell.SettingsChanged += settings =>
        {
            if (settings.TranslationLogRetentionDays != _logDays)
            {
                SweepTranslationLog();
            }
        };
    }

    private void Sweep()
    {
        var shell = _shell!;
        var service = new RetentionService(shell.Store, shell.Images, TimeProvider.System);
        var days = Math.Max(1, shell.Settings.ImageRetentionDays);

        Task.Run(() =>
        {
            try
            {
                var result = service.Sweep(
                    TimeSpan.FromDays(days),
                    shell.Settings.ProtectEntries && shell.Settings.ProtectFavorites,
                    shell.Settings.ProtectEntries && shell.Settings.ProtectPinned);
                Log.Event(LogEvent.RetentionSwept, ("removed", result.Removed));
            }
            catch (Exception failure)
            {
                // Housekeeping failing is not worth interrupting the user
                // over; the next sweep will try again. 留一行日志（O-24）：
                // 图片目录悄悄堆满往往只有它知道原因。
                Log.Event(LogEvent.RetentionSweepFailed, failure, ("days", days));
            }
        });
    }

    /// <summary>
    /// 到期的翻译记录：按设置的周期删旧的（「永不」时不动）。与图片清扫一样在后台跑，一样
    /// 失败只留日志——下一次清扫会再试。
    /// </summary>
    private void SweepTranslationLog()
    {
        var shell = _shell!;
        var days = _logDays = shell.Settings.TranslationLogRetentionDays;
        var log = shell.TranslationLog;

        Task.Run(() =>
        {
            try
            {
                Log.Event(LogEvent.RetentionSwept, ("removed", log.Sweep()), ("translationLog", true));
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.RetentionSweepFailed, failure, ("days", days), ("translationLog", true));
            }
        });
    }

    /// <summary>原 OnExit 的第一步清理：计时器停，不再排队新的清扫。</summary>
    public void Shutdown() => _timer?.Stop();
}
