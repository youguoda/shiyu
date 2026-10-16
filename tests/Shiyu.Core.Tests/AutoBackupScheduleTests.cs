using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 定期备份的时间表（评审 2026-10-16 F-20）：每周一份、失败隔天再试、只留最近几份；"上次"看文件名，
/// 只认自己的命名，文件夹里别的文件一概不碰。
/// </summary>
public class AutoBackupScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 21, 30, 0);

    [Fact]
    public void A_file_name_carries_its_moment_and_reads_back()
    {
        var name = AutoBackupSchedule.FileName(Now);

        Assert.Equal("拾语自动备份 2026-10-16 213000.shiyubk", name);
        Assert.True(AutoBackupSchedule.TryReadStamp(name, out var stamp));
        Assert.Equal(Now, stamp);
        Assert.True(AutoBackupSchedule.TryReadStamp(@"D:\备份\" + name, out _));
    }

    [Theory]
    [InlineData("拾语备份 2026-10-16.shiyubk")]
    [InlineData("拾语自动备份 2026-10-16.shiyubk")]
    [InlineData("拾语自动备份 2026-13-40 999999.shiyubk")]
    [InlineData("拾语自动备份 2026-10-16 213000.zip")]
    [InlineData("notes.txt")]
    public void Anything_not_written_by_the_schedule_is_not_ours(string name)
        => Assert.False(AutoBackupSchedule.TryReadStamp(name, out _));

    [Fact]
    public void With_no_backup_yet_it_is_due()
        => Assert.True(AutoBackupSchedule.IsDue(null, null, Now));

    [Fact]
    public void A_backup_younger_than_a_week_is_enough()
    {
        Assert.False(AutoBackupSchedule.IsDue(Now.AddDays(-6.9), null, Now));
        Assert.True(AutoBackupSchedule.IsDue(Now.AddDays(-7), null, Now));
    }

    [Fact]
    public void After_a_failure_it_waits_a_day_before_trying_again()
    {
        Assert.False(AutoBackupSchedule.IsDue(null, Now.AddHours(-2), Now));
        Assert.True(AutoBackupSchedule.IsDue(null, Now.AddHours(-24), Now));

        // 失败过但手里的备份还新鲜，照样不到时候。
        Assert.False(AutoBackupSchedule.IsDue(Now.AddDays(-1), Now.AddHours(-30), Now));
    }

    [Fact]
    public void The_newest_of_our_files_is_the_last_backup()
    {
        var names = new[]
        {
            AutoBackupSchedule.FileName(Now.AddDays(-14)),
            AutoBackupSchedule.FileName(Now.AddDays(-7)),
            "拾语备份 2099-01-01.shiyubk",
            "readme.txt",
        };

        Assert.Equal(Now.AddDays(-7), AutoBackupSchedule.Newest(names));
        Assert.Null(AutoBackupSchedule.Newest(["readme.txt"]));
    }

    [Fact]
    public void Pruning_keeps_the_newest_few_and_never_touches_other_files()
    {
        var names = Enumerable.Range(0, 6)
            .Select(week => AutoBackupSchedule.FileName(Now.AddDays(-7 * week)))
            .Append("拾语备份 2020-01-01.shiyubk")
            .Append("photo.png")
            .ToList();

        var doomed = AutoBackupSchedule.ToPrune(names, keep: 4);

        Assert.Equal(
            [AutoBackupSchedule.FileName(Now.AddDays(-28)), AutoBackupSchedule.FileName(Now.AddDays(-35))],
            doomed);
    }

    [Fact]
    public void A_keep_count_out_of_range_still_leaves_at_least_one()
    {
        var names = new[] { AutoBackupSchedule.FileName(Now), AutoBackupSchedule.FileName(Now.AddDays(-7)) };

        Assert.Equal([AutoBackupSchedule.FileName(Now.AddDays(-7))], AutoBackupSchedule.ToPrune(names, keep: 0));
    }

    // ── 设置：口令不随备份走，恢复时整套用本机的 ──

    [Fact]
    public void Auto_backup_is_off_by_default_and_keeps_four()
    {
        Assert.True(AppSettings.TryParse("{}", out var fresh));
        Assert.False(fresh.AutoBackupEnabled);
        Assert.Equal(string.Empty, fresh.AutoBackupFolder);
        Assert.Equal(4, fresh.AutoBackupKeep);
        Assert.Equal(string.Empty, fresh.AutoBackupPassword);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_protected_password_never_rides_along_in_a_backup(bool includeKey)
    {
        var settings = new AppSettings { AutoBackupEnabled = true, AutoBackupPassword = AppSettings.SecretMarker + "AAAA" };

        var json = settings.ToBackupJson(includeKey);

        Assert.DoesNotContain(AppSettings.SecretMarker + "AAAA", json);
        Assert.True(AppSettings.TryParse(json, out var carried));
        Assert.Equal(string.Empty, carried.AutoBackupPassword);
    }

    [Fact]
    public void Restoring_a_backup_keeps_this_machines_auto_backup_setup()
    {
        var local = new AppSettings
        {
            AutoBackupEnabled = true,
            AutoBackupFolder = @"D:\备份",
            AutoBackupKeep = 8,
            AutoBackupPassword = AppSettings.SecretMarker + "local",
        };
        var fromBackup = new AppSettings { AutoBackupEnabled = false, AutoBackupFolder = @"E:\elsewhere", HistoryRetentionDays = 30 };

        var restored = fromBackup.KeepingAutoBackupOf(local);

        Assert.True(restored.AutoBackupEnabled);
        Assert.Equal(@"D:\备份", restored.AutoBackupFolder);
        Assert.Equal(8, restored.AutoBackupKeep);
        Assert.Equal(AppSettings.SecretMarker + "local", restored.AutoBackupPassword);
        Assert.Equal(30, restored.HistoryRetentionDays);
    }
}
