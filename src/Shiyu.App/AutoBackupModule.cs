using System.Globalization;
using System.IO;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 定期备份（评审 2026-10-16 F-20）：开着、选了文件夹，就每周往那里写一份，保留最近几份。启动两分钟后看一眼，
/// 之后每小时看一眼到没到时候（<see cref="AutoBackupSchedule"/>）；真正的活在后台线程上。
///
/// 失败只记日志、托盘提示一次——同一段连续失败里不再提示，下一次成功之后才重新算；隔一天再试。
/// 「立即备份一次」走同一条路，结果写在设置那一行上，不弹托盘。
/// </summary>
internal sealed class AutoBackupModule
{
    private AppShell? _shell;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private DateTime? _lastFailure;
    private bool _failureTold;
    private int _running;

    /// <summary>备份跑完（成功或失败）。可能在后台线程上触发，订阅方自己回界面线程。</summary>
    public event Action? Finished;

    public void Start(AppShell shell)
    {
        _shell = shell;

        // 不跟启动抢：开机自启的那几秒里，磁盘和界面都更要紧。
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            RunIfDue();
        };
        _timer.Start();
    }

    public void Shutdown() => _timer?.Stop();

    /// <summary>设置页那一行的状态：上次是什么时候、文件夹里有几份。文件夹读不了时如实说。</summary>
    public static string Describe(AppSettings settings)
    {
        var folder = settings.AutoBackupFolder.Trim();
        if (folder.Length == 0)
        {
            return "还没选文件夹。";
        }

        if (!TryList(folder, out var names))
        {
            return "现在读不了这个文件夹（移动硬盘没插？没有权限？）。";
        }

        var ours = names.Where(name => AutoBackupSchedule.TryReadStamp(name, out _)).ToList();
        return AutoBackupSchedule.Newest(ours) is { } newest
            ? $"上次：{newest.ToString("M'月'd'日' HH':'mm", CultureInfo.InvariantCulture)} · 文件夹里有 {ours.Count} 份。"
            : "还没备份过。";
    }

    private void RunIfDue()
    {
        var settings = _shell!.Settings;
        var folder = settings.AutoBackupFolder.Trim();
        if (!settings.AutoBackupEnabled || folder.Length == 0)
        {
            return;
        }

        // 文件夹此刻读不了（移动硬盘没插）就当一份都没有：到点照常试，试不成按失败算、隔天再试。
        var newest = TryList(folder, out var names) ? AutoBackupSchedule.Newest(names) : null;
        if (AutoBackupSchedule.IsDue(newest, _lastFailure, DateTime.Now))
        {
            _ = RunAsync(manual: false);
        }
    }

    /// <summary>
    /// 备份一份并修剪旧的。定时到点与「立即备份一次」共用；返回给设置行的一句话。同一时刻只跑一份。
    /// </summary>
    public async Task<string> RunAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return "正在备份，请稍候。";
        }

        var shell = _shell!;
        var settings = shell.Settings;
        try
        {
            var folder = settings.AutoBackupFolder.Trim();
            if (folder.Length == 0)
            {
                return "还没选文件夹。";
            }

            var json = settings.ToBackupJson(includeKey: false);
            var (counts, pruned) = await Task.Run(() => Backup(
                shell.Store, folder, json, settings.AutoBackupPassword, settings.AutoBackupKeep));

            _lastFailure = null;
            _failureTold = false;
            Log.Event(LogEvent.AutoBackupSaved,
                ("entries", counts.Entries), ("images", counts.Images), ("pruned", pruned), ("manual", manual));
            return $"已备份 {counts.Entries} 条、{counts.Images} 张原图（{counts.Bytes / 1024} KB）。";
        }
        catch (Exception failure)
        {
            _lastFailure = DateTime.Now;
            Log.Event(LogEvent.AutoBackupFailed, failure, ("manual", manual));
            var reason = Reason(failure);
            if (!manual && !_failureTold)
            {
                _failureTold = true;
                shell.TellUser($"定期备份没能完成：{reason}。明天再试，详情在日志里。");
            }

            return $"没能备份：{reason}。";
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            Finished?.Invoke();
        }
    }

    private static (BackupCounts Counts, int Pruned) Backup(
        EntryStore store, string folder, string settingsJson, string protectedPassword, int keep)
    {
        Directory.CreateDirectory(folder);

        byte[]? password = null;
        if (protectedPassword.Length > 0)
        {
            password = BackupPasswordVault.Unprotect(protectedPassword)
                ?? throw new BackupException("口令在这台电脑上打不开了，请在设置里重新设置口令");
        }

        BackupCounts counts;
        try
        {
            counts = BackupArchive.Export(
                Path.Combine(folder, AutoBackupSchedule.FileName(DateTime.Now)),
                store, AppPaths.ImageDirectory, settingsJson, password);
        }
        finally
        {
            // Export 自己会抹；走不到它（路径出错一类）时这里补一刀。
            if (password is not null)
            {
                Array.Clear(password);
            }
        }

        var pruned = 0;
        if (TryList(folder, out var names))
        {
            foreach (var name in AutoBackupSchedule.ToPrune(names, keep))
            {
                try
                {
                    File.Delete(Path.Combine(folder, name));
                    pruned++;
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // 删不掉的旧备份留着，下次再删；这一份已经写好了，不算失败。
                    Log.Event(LogEvent.AutoBackupFailed, failure, ("prune", 1));
                }
            }
        }

        return (counts, pruned);
    }

    private static bool TryList(string folder, out List<string> names)
    {
        try
        {
            names = [.. Directory.EnumerateFiles(folder, "*.shiyubk").Select(Path.GetFileName).OfType<string>()];
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // expected: 文件夹不在了、没权限、路径写坏了——调用方按"读不了"处理。
            names = [];
            return false;
        }
    }

    /// <summary>失败的人话原因。</summary>
    private static string Reason(Exception failure) => failure switch
    {
        BackupException backup => backup.Message,
        UnauthorizedAccessException => "没有写入这个文件夹的权限",
        DirectoryNotFoundException or DriveNotFoundException => "找不到这个文件夹（移动硬盘没插？）",
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => "磁盘空间不够了",
        IOException => "写入文件夹时出错",
        _ => "出了意外",
    };
}
