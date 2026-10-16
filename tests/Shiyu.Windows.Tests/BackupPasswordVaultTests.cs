using System.Text;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.Windows.Tests;

/// <summary>定期备份的口令（评审 2026-10-16 F-20）：本机 DPAPI 保护，进出都是字节。</summary>
public class BackupPasswordVaultTests
{
    [Fact]
    public void A_password_round_trips_as_bytes()
    {
        var password = Encoding.Unicode.GetBytes("correct horse");

        var stored = BackupPasswordVault.Protect(password);

        Assert.NotNull(stored);
        Assert.StartsWith(AppSettings.SecretMarker, stored);
        Assert.DoesNotContain("correct horse", stored);
        Assert.Equal(password, BackupPasswordVault.Unprotect(stored!));
    }

    [Fact]
    public void Something_that_is_not_a_protected_blob_does_not_open()
    {
        Assert.Null(BackupPasswordVault.Unprotect("plain text"));
        Assert.Null(BackupPasswordVault.Unprotect(AppSettings.SecretMarker + "not base64 !"));
        Assert.Null(BackupPasswordVault.Unprotect(AppSettings.SecretMarker + Convert.ToBase64String([1, 2, 3])));
    }

    [Fact]
    public void A_blob_protected_for_the_api_key_does_not_open_as_a_backup_password()
    {
        // 两份 entropy 各管各的：API 密钥的保护形式不能被当成备份口令解开。
        var apiKeyBlob = new DpapiSecretProtector().Protect("sk-test");

        Assert.NotNull(apiKeyBlob);
        Assert.Null(BackupPasswordVault.Unprotect(apiKeyBlob!));
    }
}
