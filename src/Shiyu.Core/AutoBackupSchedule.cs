using System.Globalization;

namespace Shiyu.Core;

/// <summary>
/// 定期备份的时间表（评审 2026-10-16 F-20：历史越久越值钱，却只有手动备份）：每周一份，写进用户选的文件夹，
/// 保留最近几份。
///
/// "上次备份是什么时候"不另记在设置里，就看文件夹里最新那份自动备份的文件名：文件就是记录，用户删了它，
/// 下一次就照常补上；也省得后台线程去写设置文件。只认自己的命名，文件夹里别的文件一概不碰。
/// </summary>
public static class AutoBackupSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    /// <summary>失败后隔多久再试：不每小时撞一次同一堵墙，也不等满一周。</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromDays(1);

    public const int DefaultKeep = 4;
    public const int MaxKeep = 52;

    private const string Prefix = "拾语自动备份 ";
    private const string Extension = ".shiyubk";
    private const string StampFormat = "yyyy-MM-dd HHmmss";

    /// <summary>这一份的文件名：时刻精确到秒，同一天点两次「立即备份」也不撞名；按名字排序即按时间排序。</summary>
    public static string FileName(DateTime localNow)
        => Prefix + localNow.ToString(StampFormat, CultureInfo.InvariantCulture) + Extension;

    /// <summary>文件名是不是自动备份写下的、写于何时。别的文件（包括手动导出的备份）一律不是。</summary>
    public static bool TryReadStamp(string fileName, out DateTime stamp)
    {
        stamp = default;
        var name = Path.GetFileName(fileName);
        return name.StartsWith(Prefix, StringComparison.Ordinal)
            && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParseExact(
                name[Prefix.Length..^Extension.Length], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out stamp);
    }

    /// <summary>文件夹里最新一份自动备份写于何时；一份都没有为 null。</summary>
    public static DateTime? Newest(IEnumerable<string> fileNames)
    {
        DateTime? newest = null;
        foreach (var name in fileNames)
        {
            if (TryReadStamp(name, out var stamp) && (newest is null || stamp > newest))
            {
                newest = stamp;
            }
        }

        return newest;
    }

    /// <summary>
    /// 到没到该备份的时候：一份都没有就到了；最新一份满一周了就到了。上次失败在那之后的，等满
    /// <see cref="RetryAfterFailure"/> 再试。
    /// </summary>
    public static bool IsDue(DateTime? newest, DateTime? lastFailure, DateTime now)
    {
        if (newest is { } last && now - last < Interval)
        {
            return false;
        }

        return lastFailure is not { } failed || now - failed >= RetryAfterFailure;
    }

    /// <summary>超出保留份数的那些，最旧的先走。只认自动备份的命名。</summary>
    public static IReadOnlyList<string> ToPrune(IEnumerable<string> fileNames, int keep)
    {
        var ours = new List<(string Name, DateTime Stamp)>();
        foreach (var name in fileNames)
        {
            if (TryReadStamp(name, out var stamp))
            {
                ours.Add((name, stamp));
            }
        }

        return
        [
            .. ours
                .OrderByDescending(file => file.Stamp)
                .Skip(Math.Clamp(keep, 1, MaxKeep))
                .Select(file => file.Name),
        ];
    }
}
