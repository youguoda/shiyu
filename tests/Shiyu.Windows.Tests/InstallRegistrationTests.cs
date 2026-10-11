using Microsoft.Win32;
using Shiyu.Windows;

namespace Shiyu.Windows.Tests;

/// <summary>
/// 安装版「应用和功能」里的版本号跟着应用内更新走（ADR-0015）。用一把一次性的假卸载键
/// （HKCU\Software\ShiyuTests\…，测完即删）演，真的拾语卸载项一个字节都不碰。
/// </summary>
public sealed class InstallRegistrationTests : IDisposable
{
    private const string Parent = @"Software\ShiyuTests";

    private readonly string _keyPath = Parent + @"\" + Guid.NewGuid().ToString("N");

    private readonly string _install = Path.Combine(Path.GetTempPath(), "ShiyuInstallTest");

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

        // The scratch parent goes too once nothing else is in it: no trace left in HKCU.
        bool empty;
        using (var parent = Registry.CurrentUser.OpenSubKey(Parent))
        {
            empty = parent is { SubKeyCount: 0, ValueCount: 0 };
        }

        if (empty)
        {
            Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
        }
    }

    private void FakeUninstallKey(string location, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue("InstallLocation", location);
        key.SetValue("DisplayVersion", version);
    }

    private string? DisplayVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue("DisplayVersion") as string;
    }

    [Fact]
    public void The_installed_copy_brings_its_version_up_to_date()
    {
        // Inno writes the location with a trailing backslash; the running copy may not.
        FakeUninstallKey(_install + @"\", "0.13.0");

        Assert.True(InstallRegistration.SyncDisplayVersion(_install, "0.14.0", _keyPath));
        Assert.Equal("0.14.0", DisplayVersion());
        Assert.False(InstallRegistration.SyncDisplayVersion(_install.ToUpperInvariant(), "0.14.0", _keyPath));
    }

    [Fact]
    public void A_copy_running_somewhere_else_leaves_the_entry_alone()
    {
        FakeUninstallKey(_install + @"\", "0.13.0");

        Assert.False(InstallRegistration.SyncDisplayVersion(Path.Combine(Path.GetTempPath(), "Portable"), "0.14.0", _keyPath));
        Assert.Equal("0.13.0", DisplayVersion());
    }

    [Fact]
    public void A_portable_copy_has_no_entry_to_touch()
    {
        Assert.False(InstallRegistration.SyncDisplayVersion(_install, "0.14.0", _keyPath));
        Assert.Null(Registry.CurrentUser.OpenSubKey(_keyPath));
    }

    [Fact]
    public void The_key_name_is_the_installer_app_id()
    {
        Assert.Equal(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{158C0196-252A-4126-8516-DC9C05C0B7BB}_is1",
            InstallRegistration.UninstallKey);
    }
}
