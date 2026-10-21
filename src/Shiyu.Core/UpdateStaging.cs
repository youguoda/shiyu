using System.IO.Compression;
using System.Security.Cryptography;

namespace Shiyu.Core;

/// <summary>
/// What a completed download left behind, read back on the next start.
/// </summary>
public sealed record PendingUpdate(string Version, string Notes);

/// <summary>
/// The files-on-disk half of updating (ticket 28). Everything here runs in the
/// data directory, never the install directory: a half-finished download,
/// a failed checksum, an interrupted extraction — none of it may leave the
/// application in a state that does not boot.
///
/// The layout under <c>&lt;data&gt;\updates</c>:
///   <c>installer.zip.partial</c> — while downloading, renamed on completion
///   <c>installer.zip</c>        — verified, waiting to be extracted
///   <c>staged\</c>              — extracted new version, ready to apply
///   <c>pending.json</c>         — the marker that a staged update exists
/// </summary>
public static class UpdateStaging
{
    public const string InstallerName = "installer.zip";

    public static string Root(string dataDirectory) => Path.Combine(dataDirectory, "updates");

    public static string InstallerPath(string dataDirectory) => Path.Combine(Root(dataDirectory), InstallerName);

    public static string StagedDirectory(string dataDirectory) => Path.Combine(Root(dataDirectory), "staged");

    public static string PendingPath(string dataDirectory) => Path.Combine(Root(dataDirectory), "pending.json");

    /// <summary>Clears every leftover before a fresh download. Throws when something still holds a file.</summary>
    public static void Reset(string dataDirectory)
    {
        var root = Root(dataDirectory);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Clears what it can after a refused or failed download. Never throws: the
    /// caller is already on its way to telling the user why, and a leftover that
    /// is still held is cleared by the next <see cref="Reset"/>.
    /// </summary>
    public static void TryReset(string dataDirectory) => TryDelete(Root(dataDirectory));

    /// <summary>
    /// Removes everything the finalizer can remove — everything except the
    /// staged directory it is running from. That one is handed to the
    /// relaunched application through <see cref="CleanStagedIfIdle"/>.
    /// </summary>
    public static void CleanAfterApply(string dataDirectory)
    {
        TryDelete(InstallerPath(dataDirectory) + ".partial");
        TryDelete(InstallerPath(dataDirectory));
        TryDelete(PendingPath(dataDirectory));
        TryDelete(Path.Combine(Root(dataDirectory), "backup"));
    }

    /// <summary>
    /// Deletes the staged directory when this process is not running from it.
    /// Called at startup: by then the finalizer that could not delete its own
    /// image is long gone, and a failure simply retries on the next start.
    /// </summary>
    public static void CleanStagedIfIdle(string dataDirectory)
    {
        var staged = StagedDirectory(dataDirectory);
        if (Directory.Exists(staged)
            && !AppContext.BaseDirectory.TrimEnd('\\').StartsWith(staged.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(staged);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            // expected: 删不掉的残余是杂物，从来不是启动问题。
        }
    }

    public static void WritePending(string dataDirectory, ReleaseManifest release)
    {
        var root = Root(dataDirectory);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            PendingPath(dataDirectory),
            $"{release.Version.Text}\n{release.Notes.Replace("\r", string.Empty)}");
    }

    public static PendingUpdate? ReadPending(string dataDirectory)
    {
        var path = PendingPath(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = File.ReadAllLines(path);
        return lines.Length >= 1
            ? new PendingUpdate(lines[0], string.Join('\n', lines.Skip(1)))
            : null;
    }

    /// <summary>
    /// The download verdict. Everything that can be checked without running
    /// anything is checked: the byte count matches what the feed promised,
    /// the SHA256 matches when the release shipped one, the archive opens,
    /// and it contains an application to install. Any miss is a full reject —
    /// a half installer never moves on to staging.
    /// </summary>
    public static (bool Ok, string Error) VerifyInstaller(
        string zipPath, long expectedSize, string? expectedSha256Hex)
    {
        if (!File.Exists(zipPath))
        {
            return (false, "安装包不存在。");
        }

        var size = new FileInfo(zipPath).Length;
        if (size != expectedSize)
        {
            return (false, $"安装包大小不符（下载到 {size} 字节，发布页为 {expectedSize} 字节）。");
        }

        if (expectedSha256Hex is { Length: > 0 } expected)
        {
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
            if (actual != expected.Trim().ToLowerInvariant().Split(' ', '\t')[0])
            {
                return (false, "安装包校验失败（SHA256 不一致），已丢弃。");
            }
        }

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (!zip.Entries.Any(entry => entry.Name.Equals("Shiyu.App.exe", StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "安装包里没有 Shiyu.App.exe，可能下错了文件。");
            }
        }
        catch (InvalidDataException)
        {
            return (false, "安装包已损坏（无法作为 zip 打开），已丢弃。");
        }
        catch (IOException)
        {
            return (false, "安装包读取失败，已丢弃。");
        }

        return (true, string.Empty);
    }

    /// <summary>Extracts the verified installer into a clean staged directory.</summary>
    public static void Extract(string zipPath, string stagedDirectory)
    {
        if (Directory.Exists(stagedDirectory))
        {
            Directory.Delete(stagedDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagedDirectory);
        ZipFile.ExtractToDirectory(zipPath, stagedDirectory, overwriteFiles: true);
    }
}
