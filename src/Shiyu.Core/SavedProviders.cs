namespace Shiyu.Core;

/// <summary>
/// 自备密钥的一家服务商（用户需求 2026-10-10：每家各存一份，选中即切换）：它的服务地址、
/// 上次用的模型、预设 Id（自定义为空）与凭据。按 <see cref="Origin"/>（scheme + host + port，
/// 票 29）区分，一家至多一份——凭据只发给它自己的来源，这条规矩从"一把密钥"沿用到"每家一把"。
/// 凭据在内存里是明文，落盘时受保护（<see cref="AppSettings.SecretProtector"/>）。
/// </summary>
public sealed record SavedProvider(string Origin, string BaseUrl, string Model, string PresetId, string ApiKey)
{
    /// <summary>记录默认的 ToString 会打出凭据——日志与调试输出里只留不是秘密的几项。</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Origin = ").Append(Origin)
            .Append(", Model = ").Append(Model)
            .Append(", PresetId = ").Append(PresetId);
        return true;
    }

    /// <summary>界面上的名字：预设的显示名；自定义地址用它的主机。</summary>
    public string DisplayName
        => ProviderPresets.Find(PresetId)?.DisplayName
            ?? ProviderPresets.All.FirstOrDefault(preset => KeyOrigin.Same(preset.BaseUrl, Origin))?.DisplayName
            ?? KeyOrigin.HostOf(Origin);
}

/// <summary>
/// 凭据打码（用户需求 2026-10-10：打码显示，点开才看全）：露出开头与末尾几位，够认出是哪一把，
/// 共享屏幕或截图时又不至于把整把密钥露出去。短到露头露尾就所剩无几的，一律只给圆点。
/// </summary>
public static class CredentialMask
{
    public static string Of(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.Length < 16
            ? new string('•', 8)
            : $"{trimmed[..6]}…{trimmed[^4..]}";
    }
}
