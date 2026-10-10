using System.Text.Json;

namespace Shiyu.Core.Tests;

/// <summary>
/// A protector whose "protection" is a marker plus base64 — enough to test
/// the settings round trip without any Windows dependency.
/// </summary>
internal sealed class FakeProtector : ISecretProtector
{
    public bool FailUnprotect { get; set; }

    public string? Protect(string plain)
        => AppSettings.SecretMarker + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(plain));

    public string? Unprotect(string stored)
    {
        if (FailUnprotect)
        {
            return null;
        }

        var payload = stored.StartsWith(AppSettings.SecretMarker, StringComparison.Ordinal)
            ? stored[AppSettings.SecretMarker.Length..]
            : stored;
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
    }
}

[Collection("settings-io")]
public class SecretProtectionTests : IDisposable
{
    private const string DeepSeek = "https://api.deepseek.com";
    private const string Zhipu = "https://open.bigmodel.cn/api/paas/v4/";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(Path.GetTempPath(), "shiyu-secret-tests-" + Guid.NewGuid().ToString("N") + ".json");

    public SecretProtectionTests()
    {
        AppSettings.SecretProtector = new FakeProtector();
    }

    public void Dispose()
    {
        AppSettings.SecretProtector = null;
        try { File.Delete(_path); } catch (IOException) { }
    }

    private static AppSettings SavedFor(string baseUrl, string key)
        => new AppSettings { BackendBaseUrl = baseUrl, BackendModel = "some-model" }.WithApiKey(key);

    /// <summary>设置文件里一份带着现成（受保护或明文）凭据的服务商——旧文件、别的机器写的文件就是这样。</summary>
    private static string FileWith(string storedKey)
        => JsonSerializer.Serialize(
            new AppSettings
            {
                BackendBaseUrl = DeepSeek,
                SavedProviders = [new SavedProvider(DeepSeek, DeepSeek, "deepseek-chat", "deepseek", storedKey)],
            },
            Indented);

    [Fact]
    public void A_saved_key_is_protected_on_disk_and_plain_in_memory()
    {
        var settings = SavedFor(DeepSeek, "sk-live-123");

        settings.Save(_path);

        var stored = File.ReadAllText(_path);
        Assert.DoesNotContain("sk-live-123", stored);
        Assert.Contains(AppSettings.SecretMarker, stored);
        Assert.Equal("sk-live-123", settings.KeyFor(DeepSeek));

        Assert.True(AppSettings.TryParse(stored, out var loaded));
        Assert.Equal("sk-live-123", loaded!.KeyFor(DeepSeek));
    }

    [Fact]
    public void Every_providers_key_is_protected_on_disk()
    {
        var settings = (SavedFor(DeepSeek, "sk-deepseek-live") with { BackendBaseUrl = Zhipu })
            .WithApiKey("sk-zhipu-live");

        settings.Save(_path);

        var stored = File.ReadAllText(_path);
        Assert.DoesNotContain("sk-deepseek-live", stored);
        Assert.DoesNotContain("sk-zhipu-live", stored);

        Assert.True(AppSettings.TryParse(stored, out var loaded));
        Assert.Equal("sk-deepseek-live", loaded!.KeyFor(DeepSeek));
        Assert.Equal("sk-zhipu-live", loaded.KeyFor(Zhipu));
    }

    [Fact]
    public void Plaintext_from_an_older_shiyu_loads_as_is_and_is_protected_on_next_save()
    {
        // 单把密钥年代的明文文件：来源齐全。
        var legacy = JsonSerializer.Serialize(
            new AppSettings { BackendBaseUrl = DeepSeek, BackendApiKey = "sk-legacy", BackendApiKeyOrigin = DeepSeek },
            Indented);

        Assert.True(AppSettings.TryParse(legacy, out var loaded));
        Assert.Equal("sk-legacy", loaded!.KeyFor(DeepSeek));

        loaded.Save(_path);
        Assert.DoesNotContain("sk-legacy", File.ReadAllText(_path));
    }

    [Fact]
    public void A_protected_key_from_the_single_key_era_migrates_readable()
    {
        var legacy = JsonSerializer.Serialize(
            new AppSettings
            {
                BackendBaseUrl = DeepSeek,
                BackendApiKey = new FakeProtector().Protect("sk-old")!,
                BackendApiKeyOrigin = DeepSeek,
            },
            Indented);

        Assert.True(AppSettings.TryParse(legacy, out var loaded));
        Assert.Equal("sk-old", loaded!.KeyFor(DeepSeek));
    }

    [Fact]
    public void A_key_this_machine_cannot_open_loads_empty_not_as_garbage()
    {
        var protector = (FakeProtector)AppSettings.SecretProtector!;
        var stored = FileWith(protector.Protect("sk-gone")!);
        protector.FailUnprotect = true;

        Assert.True(AppSettings.TryParse(stored, out var loaded));
        Assert.Equal(string.Empty, loaded!.KeyFor(DeepSeek));
        Assert.Empty(loaded.SavedProviders);
    }

    [Fact]
    public void Without_a_protector_a_stored_key_is_dropped_rather_than_sent_as_a_credential()
    {
        var protectedJson = FileWith(new FakeProtector().Protect("sk-any")!);

        AppSettings.SecretProtector = null;

        Assert.True(AppSettings.TryParse(protectedJson, out var loaded));
        Assert.Equal(string.Empty, loaded!.KeyFor(DeepSeek));

        // And saving stays plaintext — a tool reading settings.json outside
        // the app must not find marker soup it cannot reproduce.
        SavedFor(DeepSeek, "sk-plain").Save(_path);
        Assert.Contains("sk-plain", File.ReadAllText(_path));
    }

    [Fact]
    public void The_backup_copy_omits_the_key_by_default_and_carries_it_only_on_request()
    {
        var settings = (SavedFor(DeepSeek, "sk-backup") with { BackendBaseUrl = Zhipu }).WithApiKey("sk-backup-2");

        var keyless = settings.ToBackupJson(includeKey: false);
        Assert.DoesNotContain("sk-backup", keyless);

        var full = settings.ToBackupJson(includeKey: true);
        Assert.Contains("sk-backup", full);
        Assert.Contains("sk-backup-2", full);
    }
}
