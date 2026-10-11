using System.IO;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 卸载程序的「同时删除数据」（ADR-0015）：<c>Shiyu.App.exe --remove-user-data</c> 删掉拾语的数据
/// 就退出，不建窗口、不进托盘。删什么由 Core 的 <see cref="UserDataRemoval"/> 定：默认位置整个删，
/// 挪去的「数据位置」只删拾语认得的。拾语还开着就一样不删——库文件正被占用，删一半比不删更糟。
/// </summary>
internal static class UserDataCommand
{
    public const string Switch = "--remove-user-data";

    /// <summary>
    /// 参数正是这个开关时执行，返回退出码：0 删干净了，1 有删不掉的，2 拾语还开着、没动。
    /// 不是这个开关返回 null，照常启动。
    /// </summary>
    public static int? TryRun(string[] arguments)
    {
        if (arguments is not [Switch])
        {
            return null;
        }

        using var instance = SingleInstance.Acquire("Shiyu");
        if (!instance.IsOnlyInstance)
        {
            return 2;
        }

        // Where the data went lives in the settings file, which always stays in the default location.
        var moved = AppSettings.Load(AppPaths.SettingsFile).DataDirectoryOverride;
        var failed = UserDataRemoval.Remove(AppPaths.DefaultRoot, moved);

        // The update helper's trace sits in %TEMP%; it is ours too.
        try
        {
            File.Delete(Path.Combine(Path.GetTempPath(), "shiyu-apply.log"));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // expected: 一份更新留下的跟踪记录删不掉，不算数据没删干净。
        }

        return failed.Count == 0 ? 0 : 1;
    }
}
