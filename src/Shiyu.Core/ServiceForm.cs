namespace Shiyu.Core;

/// <summary>
/// 服务页表单此刻的值（设置窗与首次引导共用，票 08 / 票 29）。两处窗口
/// 只负责把自己控件里的值交过来，"凭据框留空 = 沿用已存的"这条规则只有
/// 这一份定义：沿用的是<b>属于表单上这个来源</b>的那一把——密钥不会随
/// "测试连接"发往别家，也不会因为切换预设而被悄悄带走。
/// </summary>
/// <param name="Saved">窗口所知的已存设置；沿用的密钥取自它。</param>
/// <param name="BaseUrl">表单上的服务地址（可能还没落盘）。</param>
/// <param name="Model">表单上的模型。</param>
/// <param name="TypedKey">凭据框里此刻的内容；空串 = 没填。</param>
public sealed record ServiceForm(AppSettings Saved, string BaseUrl, string Model, string TypedKey)
{
    /// <summary>
    /// 记录默认的 ToString 会打出每个成员——这里既有刚敲进去的密钥，也有带着
    /// 已存密钥的整份设置。只留不是秘密的两项，日志与调试输出里不会出现密钥。
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("BaseUrl = ").Append(BaseUrl).Append(", Model = ").Append(Model);
        return true;
    }

    /// <summary>
    /// 表单上此刻能用的密钥：填了就用填的；留空则沿用已存的——来源与
    /// <see cref="BaseUrl"/> 一致才沿用，否则是空串。
    /// </summary>
    public string Key => TypedKey.Length > 0 ? TypedKey : Saved.KeyFor(BaseUrl);

    /// <summary>可直接交给探针的一份选项（地址与模型去首尾空白，一如既往）。</summary>
    public TranslationBackendOptions Options => new(BaseUrl.Trim(), Model.Trim(), Key);

    /// <summary>凭据框空着，已存的密钥却属于另一家。</summary>
    public bool SavedKeyBelongsElsewhere
        => TypedKey.Length == 0 && Saved.HasKeyForOtherOrigin(BaseUrl);

    /// <summary>
    /// 「测试连接」该说"请先填写这家服务商的密钥"（第四种结果）：地址与
    /// 模型都填了，只差这家的密钥。地址或模型没填时先说老话（请先填好…）。
    /// </summary>
    public bool NeedsOwnKey
        => SavedKeyBelongsElsewhere
            && !string.IsNullOrWhiteSpace(BaseUrl)
            && !string.IsNullOrWhiteSpace(Model);

    /// <summary>
    /// 凭据框下方的提示；用不着提示时为 null（密钥配得上、正在填、或根本没有
    /// 已存的）。<paramref name="providerName"/> 是预设的显示名，自定义地址
    /// 时传 null，改用地址里的主机。
    /// </summary>
    public string? KeyHint(string? providerName)
    {
        if (!SavedKeyBelongsElsewhere)
        {
            return null;
        }

        var current = providerName is { Length: > 0 }
            ? providerName
            : KeyOrigin.HostOf(KeyOrigin.Of(BaseUrl));
        var owner = KeyOrigin.HostOf(Saved.BackendApiKeyOrigin);

        // 只有协议（http ↔ https）不同时主机名一样，说成"属于 x，请填写 x"
        // 谁也看不懂——这时把来源写全。
        if (owner.Length > 0 && string.Equals(owner, current, StringComparison.OrdinalIgnoreCase))
        {
            owner = KeyOrigin.Of(Saved.BackendApiKeyOrigin);
            current = KeyOrigin.Of(BaseUrl);
        }

        return owner.Length == 0
            ? $"已保存的密钥没有对应的服务商，请填写 {current} 的密钥。"
            : $"已保存的密钥属于 {owner}，请填写 {current} 的密钥。";
    }
}
