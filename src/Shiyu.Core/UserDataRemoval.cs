using System.Text.RegularExpressions;

namespace Shiyu.Core;

/// <summary>
/// 卸载时「同时删除数据」（ADR-0015）：删拾语自己的东西，别的一样不碰。
///
/// 默认位置（%LOCALAPPDATA%\Shiyu）整个是拾语的——设置、凭据、日志、更新暂存都在那里——整个删。
/// 「数据位置」挪去的文件夹是用户自己选的，里面可能还放着别的东西：只删拾语认得的那几样（历史库、
/// 按拾语的命名存的图片与日志、更新暂存、两份提示记录），删完空了才连文件夹一起删。认不出来的
/// 文件一律留着——多留一个文件是小事，删错一个是大事。
/// </summary>
public static partial class UserDataRemoval
{
    /// <summary>拾语直接放在数据目录里的文件。</summary>
    private static readonly string[] OwnedFiles =
    [
        "history.db", "history.db-wal", "history.db-shm", "history.db-journal",
        "hints.json", "keymap-hints.json", "startup-error.log",
    ];

    /// <summary>更新暂存目录里拾语会放的东西（<see cref="UpdateStaging"/>）。</summary>
    private static readonly string[] OwnedUpdateEntries =
    [
        UpdateStaging.InstallerName, UpdateStaging.InstallerName + ".partial", "pending.json", "staged", "backup",
    ];

    /// <summary>
    /// 删掉拾语的数据：<paramref name="defaultRoot"/> 整个删；<paramref name="movedDirectory"/>
    /// （设置里的「数据位置」，没挪过就是空）只删拾语认得的。缺的就当已经删了。
    /// </summary>
    /// <returns>没删掉的路径（被占用、没权限）；全删干净时为空。</returns>
    public static IReadOnlyList<string> Remove(string defaultRoot, string? movedDirectory)
    {
        var failed = new List<string>();

        if (!string.IsNullOrWhiteSpace(movedDirectory)
            && Directory.Exists(movedDirectory)
            && !IsSameOrInside(movedDirectory, defaultRoot))
        {
            RemoveOwnedEntries(movedDirectory, failed);
        }

        TryDeleteDirectory(defaultRoot, failed);
        return failed;
    }

    private static void RemoveOwnedEntries(string directory, List<string> failed)
    {
        foreach (var name in OwnedFiles)
        {
            TryDeleteFile(Path.Combine(directory, name), failed);
        }

        RemoveMatching(Path.Combine(directory, "images"), ArchivedImage(), failed);
        RemoveMatching(Path.Combine(directory, "logs"), DailyLog(), failed);

        var updates = UpdateStaging.Root(directory);
        foreach (var name in OwnedUpdateEntries)
        {
            var path = Path.Combine(updates, name);
            if (Directory.Exists(path))
            {
                TryDeleteDirectory(path, failed);
            }
            else
            {
                TryDeleteFile(path, failed);
            }
        }

        TryDeleteIfEmpty(updates, failed);
        TryDeleteIfEmpty(directory, failed);
    }

    /// <summary>一个子文件夹里按拾语的命名存的文件删掉；文件夹空了一并删。</summary>
    private static void RemoveMatching(string directory, Regex owned, List<string> failed)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (owned.IsMatch(Path.GetFileName(file)))
                {
                    TryDeleteFile(file, failed);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // expected: 读不了这个文件夹——交回调用方，卸载程序会请用户手动删。
            failed.Add(directory);
            return;
        }

        TryDeleteIfEmpty(directory, failed);
    }

    /// <summary>被挪去的目录就是默认位置，或在它里面：整个删默认位置时已经带走。</summary>
    private static bool IsSameOrInside(string directory, string root)
    {
        var path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path, List<string> failed)
    {
        // Missing is success — and File.Delete would call a missing folder a failure.
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // expected: 被占用或没权限——交回调用方，卸载程序会请用户手动删。
            failed.Add(path);
        }
    }

    private static void TryDeleteDirectory(string path, List<string> failed)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // expected: 里面有文件被占用或没权限——交回调用方，卸载程序会请用户手动删。
            failed.Add(path);
        }
    }

    private static void TryDeleteIfEmpty(string path, List<string> failed)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            TryDeleteDirectory(path, failed);
        }
    }

    /// <summary>图片库的原图：yyyyMMdd-HHmmss-六位十六进制.png（<see cref="ImageArchive"/>）。</summary>
    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{6}\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchivedImage();

    /// <summary>按天滚动的日志：shiyu-yyyyMMdd.log（<see cref="FileLogSink"/>）。</summary>
    [GeneratedRegex(@"^shiyu-\d{8}\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex DailyLog();
}
