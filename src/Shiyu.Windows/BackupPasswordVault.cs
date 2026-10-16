using System.Security.Cryptography;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// 定期备份的口令在本机的存法（评审 2026-10-16 F-20）：没人值守的备份问不了口令，只能存下来。与 API 密钥
/// 同为 DPAPI 本机本用户（ADR-0011），另用一份 entropy；进出都是字节（UTF-16LE），不经过托管字符串——
/// 与手动备份「口令从不成为字符串」的纪律一致（<see cref="BackupArchive.Export"/> 用完即抹）。
/// </summary>
public static class BackupPasswordVault
{
    private static readonly byte[] Entropy = "Shiyu.AutoBackupPassword.v1"u8.ToArray();

    /// <summary>受保护的形式（标记打头）；保护失败为 null。传进来的字节由调用方抹掉。</summary>
    public static string? Protect(byte[] passwordUtf16)
    {
        try
        {
            var blob = ProtectedData.Protect(passwordUtf16, Entropy, DataProtectionScope.CurrentUser);
            return AppSettings.SecretMarker + Convert.ToBase64String(blob);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>口令的字节；不是受保护的形式、或换了用户 / 机器打不开时为 null。用完由调用方抹掉。</summary>
    public static byte[]? Unprotect(string stored)
    {
        if (!stored.StartsWith(AppSettings.SecretMarker, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var blob = Convert.FromBase64String(stored[AppSettings.SecretMarker.Length..]);
            return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // 另一台电脑或另一个用户保护的，这里打不开。
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
