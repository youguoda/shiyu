using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 自备密钥每家各存一份、选中即切换（用户需求 2026-10-10）。凭据只发给它自己的来源（票 29）
/// 的那一组测试在 KeyOriginTests；这里是"切换"与"显示"的那一半。
/// </summary>
public class SavedProvidersTests
{
    private const string DeepSeek = "https://api.deepseek.com";
    private const string Zhipu = "https://open.bigmodel.cn/api/paas/v4";

    private static ProviderPreset Preset(string id) => ProviderPresets.Find(id)!;

    /// <summary>DeepSeek（用的是备选模型）与智谱各存了一份，当前在智谱上。</summary>
    private static AppSettings TwoSaved()
        => (new AppSettings { BackendBaseUrl = DeepSeek, BackendModel = "deepseek-v4-pro", BackendPresetId = "deepseek" }
                .WithApiKey("sk-deepseek-1234567890")
            with
            { BackendBaseUrl = Zhipu, BackendModel = "glm-4-flash-250414", BackendPresetId = "zhipu" })
            .WithApiKey("sk-zhipu-0987654321");

    [Fact]
    public void Switching_to_a_saved_provider_brings_back_its_address_model_and_key()
    {
        var settings = TwoSaved();
        var deepSeek = settings.SavedProviders.Single(provider => provider.PresetId == "deepseek");

        var switched = settings.SwitchedTo(deepSeek);

        Assert.Equal(DeepSeek, switched.BackendBaseUrl);
        Assert.Equal("deepseek-v4-pro", switched.BackendModel);
        Assert.Equal("deepseek", switched.BackendPresetId);
        Assert.Equal("sk-deepseek-1234567890", switched.Backend.ApiKey);
        Assert.True(switched.Backend.IsConfigured);
    }

    [Fact]
    public void Picking_a_saved_preset_returns_to_the_model_used_last_time()
    {
        var switched = TwoSaved().SwitchedToPreset(Preset("deepseek"));

        Assert.Equal("deepseek-v4-pro", switched.BackendModel);
        Assert.Equal("sk-deepseek-1234567890", switched.Backend.ApiKey);
    }

    [Fact]
    public void Picking_a_preset_never_saved_takes_its_defaults_and_has_no_key_yet()
    {
        var switched = TwoSaved().SwitchedToPreset(Preset("bailian"));

        Assert.Equal(Preset("bailian").BaseUrl, switched.BackendBaseUrl);
        Assert.Equal(Preset("bailian").DefaultModel, switched.BackendModel);
        Assert.False(switched.Backend.IsConfigured);
        Assert.True(switched.HasKeyForOtherOrigin(switched.BackendBaseUrl));
    }

    [Fact]
    public void A_changed_model_is_remembered_for_the_provider_in_use()
    {
        var onZhipu = TwoSaved() with { BackendModel = "glm-4.7-flash" };

        var remembered = onZhipu.RememberingActiveProvider();

        var zhipu = remembered.SavedProviders.Single(provider => provider.PresetId == "zhipu");
        Assert.Equal("glm-4.7-flash", zhipu.Model);
        Assert.Equal("sk-zhipu-0987654321", zhipu.ApiKey);

        // 别家的一份原样不动；没存过的地址什么也不记。
        Assert.Equal(TwoSaved().SavedProviders[0], remembered.SavedProviders[0]);
        var elsewhere = remembered with { BackendBaseUrl = "https://llm.example.com/v1" };
        Assert.Same(elsewhere, elsewhere.RememberingActiveProvider());
    }

    [Fact]
    public void Removing_a_provider_takes_only_that_one_away()
    {
        var settings = TwoSaved();

        var removed = settings.WithoutProvider(KeyOrigin.Of(Zhipu));

        var left = Assert.Single(removed.SavedProviders);
        Assert.Equal("deepseek", left.PresetId);
        Assert.False(removed.Backend.IsConfigured);
    }

    [Fact]
    public void Providers_are_named_by_preset_then_by_host()
    {
        var settings = TwoSaved().SwitchedToPreset(Preset("deepseek")) with
        {
            BackendBaseUrl = "https://llm.example.com:8443/v1",
            BackendPresetId = string.Empty,
        };
        var withCustom = settings.WithApiKey("sk-custom-1234567890");

        Assert.Equal(
            ["DeepSeek", "智谱 GLM（免费模型）", "llm.example.com:8443"],
            withCustom.SavedProviders.Select(provider => provider.DisplayName));
    }

    [Fact]
    public void A_saved_provider_never_prints_its_key()
    {
        var text = TwoSaved().SavedProviders[0].ToString();

        Assert.DoesNotContain("sk-deepseek", text);
        Assert.Contains("api.deepseek.com", text);
    }

    [Theory]
    [InlineData("sk-4f2c81d0e9b3477f7a9a1b", "sk-4f2…9a1b")]
    [InlineData("  sk-4f2c81d0e9b3477f7a9a1b  ", "sk-4f2…9a1b")]
    [InlineData("short-key", "••••••••")]
    [InlineData("", "")]
    public void A_key_shows_its_ends_only_and_short_ones_show_nothing(string key, string shown)
        => Assert.Equal(shown, CredentialMask.Of(key));
}
