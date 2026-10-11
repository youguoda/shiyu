using System.IO;
using Microsoft.Win32;

namespace Shiyu.Windows;

/// <summary>
/// 安装版在「应用和功能」里的那一行（ADR-0015）。Inno Setup 装的拾语在当前用户的卸载项里有一把
/// <c>{AppId}_is1</c> 键；应用内更新原地换掉了文件，那一行的版本号却还停在装的时候——启动时对一下。
/// 便携版（解压的 zip）没有这把键，什么都不做；键里记的安装位置不是正在运行的这一份，也不碰。
/// </summary>
public static class InstallRegistration
{
    /// <summary>安装包的身份，与 installer\Shiyu.iss 的 AppId 是同一个，永远不改。</summary>
    public const string AppId = "{158C0196-252A-4126-8516-DC9C05C0B7BB}";

    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

    /// <summary>
    /// 卸载项记的安装位置就是 <paramref name="installDirectory"/> 时，把它的 DisplayVersion 改成
    /// <paramref name="version"/>。改了返回 true；不是安装版、装在别处、本来就对，都返回 false。
    /// </summary>
    public static bool SyncDisplayVersion(string installDirectory, string version, string keyPath = UninstallKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key?.GetValue("InstallLocation") is not string location
                || !SameDirectory(location, installDirectory)
                || key.GetValue("DisplayVersion") as string == version)
            {
                return false;
            }

            key.SetValue("DisplayVersion", version);
            return true;
        }
        catch (Exception failure) when (
            failure is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // expected: 卸载项被策略锁住——「应用和功能」里的版本号旧一点，不值得拦启动。
            return false;
        }
    }

    private static bool SameDirectory(string a, string b)
    {
        static string Normal(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // expected: 卸载项里记了个不成路径的东西——当作不是这一份。
            return false;
        }
    }
}
