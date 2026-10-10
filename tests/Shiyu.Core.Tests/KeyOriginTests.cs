using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 票 29：自备密钥只发给它所属的服务商。"来源"是服务地址的 scheme + host +
/// port——只比来源、不比路径：同一家常有多个路径前缀（/v1、
/// /compatible-mode/v1），改个路径不该让密钥失效；不同的服务商从不共用主机。
/// </summary>
public class KeyOriginTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/", "https://api.deepseek.com")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope.aliyuncs.com")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4/", "https://open.bigmodel.cn")]
    [InlineData("  https://api.siliconflow.cn/v1  ", "https://api.siliconflow.cn")]
    public void The_origin_is_scheme_host_and_port_without_the_path(string baseUrl, string origin)
        => Assert.Equal(origin, KeyOrigin.Of(baseUrl));

    [Fact]
    public void Query_and_fragment_never_enter_the_origin()
    {
        var origin = KeyOrigin.Of("https://api.example.com/v1?key=abc123#frag");

        Assert.Equal("https://api.example.com", origin);
        Assert.DoesNotContain("abc123", origin);
    }

    [Fact]
    public void The_default_port_is_the_same_origin_and_any_other_port_is_not()
    {
        Assert.Equal(KeyOrigin.Of("https://api.deepseek.com"), KeyOrigin.Of("https://api.deepseek.com:443/v1"));
        Assert.Equal(KeyOrigin.Of("http://api.example.com"), KeyOrigin.Of("http://api.example.com:80/v1"));

        Assert.Equal("https://api.example.com:8443", KeyOrigin.Of("https://api.example.com:8443/v1"));
        Assert.NotEqual(KeyOrigin.Of("https://api.example.com"), KeyOrigin.Of("https://api.example.com:8443"));

        // 本地服务（Ollama、LM Studio）的端口就是它的身份。
        Assert.Equal("http://localhost:11434", KeyOrigin.Of("http://localhost:11434/v1"));
        Assert.Equal("http://127.0.0.1:18731", KeyOrigin.Of("http://127.0.0.1:18731/v1"));
    }

    [Fact]
    public void The_scheme_is_part_of_the_origin()
        => Assert.NotEqual(
            KeyOrigin.Of("http://api.deepseek.com/v1"),
            KeyOrigin.Of("https://api.deepseek.com/v1"));

    [Fact]
    public void Scheme_and_host_case_do_not_matter()
        => Assert.Equal(
            KeyOrigin.Of("https://api.deepseek.com/v1"),
            KeyOrigin.Of("HTTPS://API.DeepSeek.com/V1"));

    [Fact]
    public void User_info_is_never_part_of_the_origin_and_never_hides_the_real_host()
    {
        // 来源是明文落盘的：地址里夹带的账号口令不能被一并抄进去。
        var withCredentials = KeyOrigin.Of("https://user:secret-pass@api.deepseek.com/v1");
        Assert.Equal("https://api.deepseek.com", withCredentials);
        Assert.DoesNotContain("secret-pass", withCredentials);

        // 经典的障眼法：@ 之前是"看起来像 A 家"的用户名，真正的主机在后面。
        Assert.Equal(
            "https://evil.example",
            KeyOrigin.Of("https://api.deepseek.com@evil.example/v1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_address_has_no_origin(string? baseUrl)
        => Assert.Equal(string.Empty, KeyOrigin.Of(baseUrl));

    [Fact]
    public void An_address_without_a_scheme_is_read_as_https_so_that_adding_one_later_keeps_the_pairing()
    {
        // 这样的地址发不出任何请求（HttpClient 要绝对地址），所以拿它做来源
        // 不会泄露什么；但补上 https:// 之后密钥应当仍然配得上。
        Assert.Equal("https://api.deepseek.com", KeyOrigin.Of("api.deepseek.com/v1"));
        Assert.Equal(KeyOrigin.Of("api.deepseek.com/v1"), KeyOrigin.Of("https://api.deepseek.com/v1"));
        Assert.Equal("https://probe.invalid", KeyOrigin.Of("probe.invalid"));
    }

    [Theory]
    [InlineData("https://")]
    [InlineData("http://")]
    [InlineData("://")]
    [InlineData("https://api.deepseek.com:abc/v1")]
    [InlineData("ftp://api.deepseek.com")]
    [InlineData("api deepseek com")]
    [InlineData("\\\\server\\share")]
    public void A_malformed_address_never_throws_and_never_collides_with_a_real_origin(string baseUrl)
    {
        var origin = KeyOrigin.Of(baseUrl);

        Assert.NotEqual(string.Empty, origin);
        Assert.NotEqual("https://api.deepseek.com", origin);

        // 确定的：同一个残缺地址每次给同一个值，配对才稳定。
        Assert.Equal(origin, KeyOrigin.Of(baseUrl));
    }

    [Fact]
    public void The_host_of_an_origin_is_what_the_hint_names()
    {
        Assert.Equal("api.deepseek.com", KeyOrigin.HostOf("https://api.deepseek.com"));
        Assert.Equal("api.example.com:8443", KeyOrigin.HostOf("https://api.example.com:8443"));
        Assert.Equal(string.Empty, KeyOrigin.HostOf(string.Empty));
        Assert.Equal(string.Empty, KeyOrigin.HostOf(null));
    }
}

/// <summary>
/// 绑定来源后的密钥语义：来源一致才带上；写下密钥必写来源；旧文件迁移；
/// 备份与恢复。落盘的用例要与别的会替换密钥保护器的测试串行。
/// </summary>
[Collection("settings-io")]
public class KeyBoundToOriginTests : IDisposable
{
    private const string DeepSeek = "https://api.deepseek.com";
    private const string Zhipu = "https://open.bigmodel.cn/api/paas/v4";
    private const string Bailian = "https://dashscope.aliyuncs.com/compatible-mode/v1";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "shiyu-key-origin-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        AppSettings.SecretProtector = null;
        try { File.Delete(_path); } catch (IOException) { }
    }

    /// <summary>"用户在这个地址上保存了一把密钥"：唯一的写入口造出来的状态。</summary>
    private static AppSettings SavedFor(string baseUrl, string key = "sk-deepseek")
        => new AppSettings { BackendBaseUrl = baseUrl, BackendModel = "some-model" }.WithApiKey(key);

    // --- 来源一致才带密钥 --------------------------------------------------------------

    [Fact]
    public void The_saved_key_is_handed_out_only_for_the_origin_it_was_saved_under()
    {
        var settings = SavedFor(DeepSeek);

        Assert.Equal("sk-deepseek", settings.KeyFor(DeepSeek));
        Assert.Equal(string.Empty, settings.KeyFor(Zhipu));
        Assert.Equal(string.Empty, settings.KeyFor(string.Empty));
    }

    [Theory]
    [InlineData(Bailian, "https://dashscope.aliyuncs.com/v1")]
    [InlineData(Bailian, "https://dashscope.aliyuncs.com/")]
    [InlineData(DeepSeek, "https://api.deepseek.com/v1/")]
    [InlineData(DeepSeek, "https://API.DeepSeek.com:443/v1")]
    public void Changing_only_the_path_keeps_the_key_valid(string saved, string edited)
    {
        var settings = SavedFor(saved, "sk-same-provider");

        Assert.Equal("sk-same-provider", settings.KeyFor(edited));
        Assert.True((settings with { BackendBaseUrl = edited }).Backend.IsConfigured);
    }

    [Theory]
    [InlineData("http://api.deepseek.com")]
    [InlineData("https://api.deepseek.com:8443")]
    [InlineData("https://api2.deepseek.com")]
    [InlineData("https://api.deepseek.com.evil.example")]
    public void A_different_scheme_host_or_port_is_a_different_service(string other)
        => Assert.Equal(string.Empty, SavedFor(DeepSeek).KeyFor(other));

    [Fact]
    public void A_foreign_origin_makes_the_backend_unconfigured_and_withholds_the_key()
    {
        // 设置·服务里把预设从 DeepSeek 切到智谱：applyPreset 只改地址与模型。
        var switched = SavedFor(DeepSeek) with
        {
            BackendPresetId = "zhipu",
            BackendBaseUrl = Zhipu,
            BackendModel = "glm-4-flash-250414",
        };

        Assert.False(switched.Backend.IsConfigured);
        Assert.Equal(string.Empty, switched.Backend.ApiKey);
        Assert.False(switched.IsTranslationConfigured);

        // 密钥本身还在（记在 DeepSeek 名下），只是不会被发往别家。
        Assert.Equal("sk-deepseek", switched.KeyFor(DeepSeek));
    }

    [Fact]
    public void Switching_away_and_back_makes_the_saved_key_usable_again_without_retyping()
    {
        var onDeepSeek = SavedFor(DeepSeek);
        var away = onDeepSeek with { BackendBaseUrl = Zhipu };
        var back = away with { BackendBaseUrl = DeepSeek };

        Assert.True(onDeepSeek.Backend.IsConfigured);
        Assert.False(away.Backend.IsConfigured);
        Assert.True(back.Backend.IsConfigured);
        Assert.Equal("sk-deepseek", back.Backend.ApiKey);
    }

    [Fact]
    public void A_key_with_no_recorded_origin_is_never_handed_out()
    {
        // 失败即关闭：谁绕开唯一的写入口只写密钥，得到的是"没配置"而不是泄露。
        var unbound = new AppSettings
        {
            BackendBaseUrl = DeepSeek,
            BackendModel = "some-model",
            BackendApiKey = "sk-unbound",
        };

        Assert.Equal(string.Empty, unbound.KeyFor(DeepSeek));
        Assert.False(unbound.Backend.IsConfigured);
    }

    [Fact]
    public void An_empty_key_is_empty_for_every_origin()
    {
        var settings = new AppSettings { BackendBaseUrl = DeepSeek, BackendApiKeyOrigin = DeepSeek };

        Assert.Equal(string.Empty, settings.KeyFor(DeepSeek));
    }

    // --- 线上：请求带不带 Authorization --------------------------------------------------

    [Fact]
    public async Task A_foreign_origin_sends_no_request_at_all_so_no_authorization_either()
    {
        var handler = new ScriptedHandler(_ => BackendTransport.Stream(
            """{"choices":[{"delta":{"content":"你好"}}]}""", "[DONE]"));
        var switched = SavedFor(DeepSeek) with { BackendBaseUrl = Zhipu, BackendModel = "glm-4-flash-250414" };
        var session = new TranslationSession(
            new OpenAiCompatibleBackend(switched.Backend, new HttpClient(handler)));

        await session.RunAsync(new TranslationRequest("hello", "Chinese"));

        // 面板先问 IsTranslationConfigured 走"还没有配置"引导卡；万一有人绕过
        // 它直接开流，后端自己也拒绝，一个字节都不会发给智谱。
        Assert.Equal(TranslationState.Failed, session.State);
        Assert.Contains("还没有配置", session.Error);
        Assert.All(handler.Requests, sent => Assert.Null(sent.Message.Headers.Authorization));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_matching_origin_carries_the_key_to_the_new_path_in_the_authorization_header()
    {
        var handler = new ScriptedHandler(_ => BackendTransport.Stream(
            """{"choices":[{"delta":{"content":"你好"}}]}""", "[DONE]"));
        var edited = SavedFor(Bailian, "sk-bailian") with
        {
            BackendBaseUrl = "https://dashscope.aliyuncs.com/v1",
        };
        var session = new TranslationSession(
            new OpenAiCompatibleBackend(edited.Backend, new HttpClient(handler)));

        await session.RunAsync(new TranslationRequest("hello", "Chinese"));

        var request = handler.Requests.Single().Message;
        Assert.Equal("https://dashscope.aliyuncs.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-bailian", request.Headers.Authorization?.Parameter);
    }

    // --- 保存密钥时写下来源 --------------------------------------------------------------

    [Fact]
    public void Saving_a_key_writes_the_origin_of_the_current_service_address()
    {
        var settings = new AppSettings { BackendBaseUrl = Bailian, BackendModel = "qwen-plus" }.WithApiKey("sk-bailian");

        var saved = Assert.Single(settings.SavedProviders);
        Assert.Equal("https://dashscope.aliyuncs.com", saved.Origin);
        Assert.Equal(Bailian, saved.BaseUrl);
        Assert.Equal("qwen-plus", saved.Model);
        Assert.Equal("sk-bailian", saved.ApiKey);
    }

    [Fact]
    public void Saving_a_key_for_another_provider_keeps_the_first_ones_too()
    {
        // 用户需求 2026-10-10：每家各存一份——存智谱的密钥不再把 DeepSeek 的顶掉。
        var onDeepSeek = SavedFor(DeepSeek, "sk-deepseek");

        var both = (onDeepSeek with { BackendBaseUrl = Zhipu }).WithApiKey("sk-zhipu");

        Assert.Equal("sk-zhipu", both.KeyFor(Zhipu));
        Assert.Equal("sk-deepseek", both.KeyFor(DeepSeek));
        Assert.Equal(2, both.SavedProviders.Count);
    }

    [Fact]
    public void Saving_again_for_the_same_provider_replaces_its_key_in_place()
    {
        var both = (SavedFor(DeepSeek, "sk-old") with { BackendBaseUrl = Zhipu }).WithApiKey("sk-zhipu");

        var replaced = (both with { BackendBaseUrl = DeepSeek + "/v1" }).WithApiKey("sk-new");

        Assert.Equal(2, replaced.SavedProviders.Count);
        Assert.Equal("sk-new", replaced.SavedProviders[0].ApiKey);
        Assert.Equal("sk-new", replaced.KeyFor(DeepSeek));
        Assert.Equal("sk-zhipu", replaced.KeyFor(Zhipu));
    }

    [Fact]
    public void Without_an_address_there_is_nowhere_to_save_a_key()
        => Assert.Empty(new AppSettings { BackendBaseUrl = "  " }.WithApiKey("sk-nowhere").SavedProviders);

    [Fact]
    public void Changing_the_address_alone_never_touches_the_saved_providers()
    {
        var onDeepSeek = SavedFor(DeepSeek);

        var switched = onDeepSeek with { BackendBaseUrl = Zhipu };

        Assert.Equal(onDeepSeek.SavedProviders, switched.SavedProviders);
    }

    [Fact]
    public void Clearing_the_key_removes_only_that_provider()
    {
        var both = (SavedFor(DeepSeek) with { BackendBaseUrl = Zhipu }).WithApiKey("sk-zhipu");

        var cleared = both.WithApiKey(string.Empty);

        Assert.Equal(string.Empty, cleared.KeyFor(Zhipu));
        Assert.Equal("sk-deepseek", cleared.KeyFor(DeepSeek));
        Assert.Empty(SavedFor(DeepSeek).WithApiKey(string.Empty).SavedProviders);
    }

    [Fact]
    public void The_origin_is_stored_in_plain_text_beside_a_protected_key()
    {
        // 票 11 不回归：密钥落盘仍是保护后的样子；来源不是秘密，明文存。
        AppSettings.SecretProtector = new FakeProtector();
        var settings = SavedFor(DeepSeek, "sk-live-123");

        settings.Save(_path);

        var stored = File.ReadAllText(_path);
        Assert.DoesNotContain("sk-live-123", stored);
        Assert.Contains(AppSettings.SecretMarker, stored);
        Assert.Contains("\"Origin\": \"https://api.deepseek.com\"", stored);

        var loaded = AppSettings.Load(_path);
        Assert.Equal("sk-live-123", loaded.KeyFor(DeepSeek));
        Assert.True(loaded.Backend.IsConfigured);
    }

    // --- 迁移：旧文件有密钥、没有来源 ----------------------------------------------------

    private const string LegacyJson = """
        {"BackendBaseUrl":"https://api.deepseek.com","BackendModel":"deepseek-flash","BackendApiKey":"sk-legacy"}
        """;

    [Fact]
    public void A_legacy_file_with_a_key_and_no_origin_is_bound_to_the_address_it_has_now()
    {
        Assert.True(AppSettings.TryParse(LegacyJson, out var loaded));

        // 并入各家各存的一份：来源取此刻的地址，地址与模型一并记下。
        var saved = Assert.Single(loaded.SavedProviders);
        Assert.Equal("https://api.deepseek.com", saved.Origin);
        Assert.Equal("deepseek-flash", saved.Model);
        Assert.Equal("sk-legacy", loaded.KeyFor(DeepSeek));
        Assert.Equal(string.Empty, loaded.BackendApiKey);

        // 维持现有的配对：升级后原有翻译照常可用。
        Assert.True(loaded.Backend.IsConfigured);
    }

    [Fact]
    public void The_migrated_origin_reaches_the_disk_on_the_next_save()
    {
        AppSettings.SecretProtector = new FakeProtector();
        File.WriteAllText(_path, LegacyJson);

        var loaded = AppSettings.Load(_path);
        loaded.Save(_path);

        var stored = File.ReadAllText(_path);
        Assert.Contains("\"Origin\": \"https://api.deepseek.com\"", stored);
        Assert.DoesNotContain("sk-legacy", stored);

        // 旧的单把字段不再写回文件。
        Assert.DoesNotContain("BackendApiKey", stored);
        Assert.Equal("sk-legacy", AppSettings.Load(_path).Backend.ApiKey);
    }

    [Fact]
    public void A_file_that_already_records_an_origin_keeps_it()
    {
        // 迁移只补空缺，从不改写已有的来源（否则切换预设后的重启会把
        // 旧密钥悄悄"洗"给新地址）。
        const string json = """
            {"BackendBaseUrl":"https://open.bigmodel.cn/api/paas/v4","BackendModel":"glm",
             "BackendApiKey":"sk-deepseek","BackendApiKeyOrigin":"https://api.deepseek.com"}
            """;

        Assert.True(AppSettings.TryParse(json, out var loaded));

        // 记在 DeepSeek 名下（地址与模型取预设），当前的智谱照旧没配置。
        var saved = Assert.Single(loaded.SavedProviders);
        Assert.Equal("https://api.deepseek.com", saved.Origin);
        Assert.Equal("deepseek", saved.PresetId);
        Assert.Equal("sk-deepseek", loaded.KeyFor(DeepSeek));
        Assert.False(loaded.Backend.IsConfigured);
    }

    [Fact]
    public void Migration_invents_nothing_when_there_is_no_key_or_no_address()
    {
        Assert.True(AppSettings.TryParse(
            """{"BackendBaseUrl":"https://api.deepseek.com"}""", out var noKey));
        Assert.Empty(noKey.SavedProviders);

        Assert.True(AppSettings.TryParse("""{"BackendApiKey":"sk-orphan"}""", out var noAddress));
        Assert.Empty(noAddress.SavedProviders);
        Assert.Equal(string.Empty, noAddress.BackendApiKey);
    }

    [Fact]
    public void A_legacy_address_without_a_scheme_stays_paired_with_its_key()
    {
        // 探针种子的形状（tools/probes/seed.ps1）：地址故意写成不带协议，让
        // 每次翻译都失败在发请求这一步。迁移不该把它变成"没配置"。
        Assert.True(AppSettings.TryParse(
            """{"BackendBaseUrl":"probe.invalid","BackendModel":"m","BackendApiKey":"probe-key"}""",
            out var loaded));

        Assert.True(loaded.Backend.IsConfigured);
    }

    // --- 备份：来源随设置走 ----------------------------------------------------------------

    [Fact]
    public void The_origin_travels_with_the_settings_in_a_backup_that_carries_the_key()
    {
        var settings = SavedFor(DeepSeek);

        Assert.True(AppSettings.TryParse(settings.ToBackupJson(includeKey: true), out var restored));

        Assert.Equal(settings.SavedProviders, restored.SavedProviders);
        Assert.Equal("sk-deepseek", restored.Backend.ApiKey);
        Assert.True(restored.Backend.IsConfigured);
    }

    [Fact]
    public void A_backup_without_the_keys_carries_no_saved_provider()
    {
        var json = SavedFor(DeepSeek).ToBackupJson(includeKey: false);

        Assert.DoesNotContain("sk-deepseek", json);
        Assert.True(AppSettings.TryParse(json, out var parsed));

        // 没有凭据的"一家"什么也恢复不了——整份不带。
        Assert.Empty(parsed.SavedProviders);
    }

    [Fact]
    public void Restoring_a_keyless_backup_keeps_this_machines_key_together_with_its_origin()
    {
        var local = SavedFor(DeepSeek);

        // 备份来自另一台机器：智谱、没带密钥（默认）。
        var backup = new AppSettings
        {
            TargetLanguage = "English",
            BackendBaseUrl = Zhipu,
            BackendModel = "glm-4-flash-250414",
        }.ToBackupJson(includeKey: false);
        Assert.True(AppSettings.TryParse(backup, out var parsed));

        var restored = parsed.KeepingKeyOf(local);

        // 设置照常恢复；本机已存的各家服务商一并留下。
        Assert.Equal("English", restored.TargetLanguage);
        Assert.Equal(Zhipu, restored.BackendBaseUrl);
        Assert.Equal(local.SavedProviders, restored.SavedProviders);

        // 备份把地址换成了智谱：本机的 DeepSeek 密钥不会被发去智谱……
        Assert.False(restored.Backend.IsConfigured);

        // ……而地址切回来，它仍然可用。
        Assert.True((restored with { BackendBaseUrl = DeepSeek }).Backend.IsConfigured);
    }

    [Fact]
    public void Restoring_a_keyless_backup_for_the_same_service_keeps_everything_working()
    {
        var local = SavedFor(DeepSeek);
        Assert.True(AppSettings.TryParse(local.ToBackupJson(includeKey: false), out var parsed));

        var restored = parsed.KeepingKeyOf(local);

        Assert.True(restored.Backend.IsConfigured);
        Assert.Equal("sk-deepseek", restored.Backend.ApiKey);
    }

    [Fact]
    public void Restoring_a_backup_that_carries_its_own_key_takes_the_pair_as_it_came()
    {
        var local = SavedFor(DeepSeek);
        var theirs = SavedFor(Zhipu, "sk-zhipu");
        Assert.True(AppSettings.TryParse(theirs.ToBackupJson(includeKey: true), out var parsed));

        var restored = parsed.KeepingKeyOf(local);

        // 备份带着它自己的各家服务商：恢复的就是备份里的那一套。
        Assert.Equal(theirs.SavedProviders, restored.SavedProviders);
        Assert.Equal("sk-zhipu", restored.Backend.ApiKey);
        Assert.True(restored.Backend.IsConfigured);
    }

    [Fact]
    public void The_origin_survives_a_real_backup_archive_round_trip_with_and_without_the_key()
    {
        // 与真正的备份文件走一遍（加密与否都是同一份设置 JSON 在包里进出）。
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);
        store.Append("some text", "explorer", DateTimeOffset.UnixEpoch);

        var images = Path.Combine(
            Path.GetTempPath(), "shiyu-key-origin-images", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(images);
        try
        {
            var settings = SavedFor(DeepSeek, "sk-in-the-vault");
            byte[] Password() => System.Text.Encoding.Unicode.GetBytes("correct horse");

            // 加密导出并勾选"同时导出 API 密钥"：包里是明文密钥与它的来源。
            var withKey = Path.Combine(images, "with-key.shiyubk");
            BackupArchive.Export(withKey, store, images, settings.ToBackupJson(includeKey: true), Password());
            var restoredWithKey = BackupArchive.Import(
                withKey, store, images, overwrite: true, Password()).SettingsJson;

            Assert.True(AppSettings.TryParse(restoredWithKey!, out var carried));
            Assert.Equal("sk-in-the-vault", carried.Backend.ApiKey);
            Assert.Equal(settings.SavedProviders, carried.SavedProviders);

            // 默认的备份：没有密钥；恢复时本机的密钥与来源留下。
            var plain = Path.Combine(images, "plain.shiyubk");
            BackupArchive.Export(plain, store, images, settings.ToBackupJson(includeKey: false), null);
            var restoredPlain = BackupArchive.Import(
                plain, store, images, overwrite: true, passwordUtf16: null).SettingsJson;

            Assert.DoesNotContain("sk-in-the-vault", restoredPlain);
            Assert.True(AppSettings.TryParse(restoredPlain!, out var keyless));
            var local = keyless.KeepingKeyOf(settings);
            Assert.Equal("sk-in-the-vault", local.Backend.ApiKey);
            Assert.Equal(settings.SavedProviders, local.SavedProviders);
        }
        finally
        {
            try { Directory.Delete(images, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_backup_made_before_origins_existed_is_bound_to_its_own_address()
    {
        // 旧版本导出的、带密钥的备份：没有来源字段。
        Assert.True(AppSettings.TryParse(LegacyJson, out var parsed));

        var restored = parsed.KeepingKeyOf(new AppSettings());

        Assert.Equal("sk-legacy", restored.Backend.ApiKey);
        Assert.True(restored.Backend.IsConfigured);
    }
}

/// <summary>
/// 设置窗与引导共用的"服务表单"：密钥框留空 = 沿用已存的，但只沿用属于
/// 表单上这个来源的那一把。两处 readForm 都只是把自己的控件值交给它。
/// </summary>
public class ServiceFormTests
{
    private const string DeepSeek = "https://api.deepseek.com";
    private const string Zhipu = "https://open.bigmodel.cn/api/paas/v4";

    private static AppSettings SavedFor(string baseUrl, string key = "sk-deepseek")
        => new AppSettings { BackendBaseUrl = baseUrl, BackendModel = "some-model" }.WithApiKey(key);

    [Fact]
    public void A_typed_key_always_wins()
    {
        var form = new ServiceForm(SavedFor(DeepSeek), Zhipu, "glm", TypedKey: "sk-typed");

        Assert.Equal("sk-typed", form.Key);
        Assert.False(form.SavedKeyBelongsElsewhere);
    }

    [Fact]
    public void A_blank_box_reuses_the_saved_key_for_its_own_origin()
    {
        var form = new ServiceForm(SavedFor(DeepSeek), "https://api.deepseek.com/v1", "m", TypedKey: "");

        Assert.Equal("sk-deepseek", form.Key);
        Assert.False(form.SavedKeyBelongsElsewhere);
    }

    [Fact]
    public void A_blank_box_over_another_origin_reads_as_an_empty_key()
    {
        // 两处 readForm 的验收：来源不一致且框空，返回空密钥——旧密钥不会
        // 随"测试连接"发给新地址。
        var form = new ServiceForm(SavedFor(DeepSeek), Zhipu, "glm", TypedKey: "");

        Assert.Equal(string.Empty, form.Key);
        Assert.Equal(string.Empty, form.Options.ApiKey);
        Assert.True(form.SavedKeyBelongsElsewhere);
    }

    [Fact]
    public void With_no_saved_key_a_blank_box_is_just_empty_not_elsewhere()
    {
        var form = new ServiceForm(new AppSettings(), Zhipu, "glm", TypedKey: "");

        Assert.Equal(string.Empty, form.Key);
        Assert.False(form.SavedKeyBelongsElsewhere);
    }

    [Fact]
    public void Printing_a_form_never_shows_a_key_it_carries()
    {
        // 记录的默认 ToString 会把每个成员都打出来：这里既有刚敲进去的密钥，
        // 也有整份已存设置（含已存的密钥）——日志或调试输出里都不该出现。
        var form = new ServiceForm(SavedFor(DeepSeek, "sk-saved-secret"), Zhipu, "glm", "sk-typed-secret");

        var printed = form.ToString();

        Assert.DoesNotContain("sk-saved-secret", printed);
        Assert.DoesNotContain("sk-typed-secret", printed);
        Assert.Contains(Zhipu, printed);
    }

    [Fact]
    public void The_options_are_trimmed_the_way_the_probe_always_sent_them()
    {
        var form = new ServiceForm(new AppSettings(), "  https://api.example.com/v1 ", " some-model ", "k");

        Assert.Equal(new TranslationBackendOptions("https://api.example.com/v1", "some-model", "k"), form.Options);
    }

    [Fact]
    public void The_hint_names_the_provider_being_filled_in()
    {
        // 别家的凭据都还在（用户需求 2026-10-10：每家各存一份）——提示只说这一家还没有。
        var form = new ServiceForm(SavedFor(DeepSeek), Zhipu, "glm", TypedKey: "");

        Assert.Equal(
            "还没有保存 智谱 GLM（免费模型） 的凭据。各家的凭据分开保存，填好后点「保存凭据」。",
            form.KeyHint("智谱 GLM（免费模型）"));
    }

    [Fact]
    public void A_custom_address_is_named_by_its_host()
    {
        var form = new ServiceForm(SavedFor(DeepSeek), "https://llm.example.com:8443/v1", "m", TypedKey: "");

        Assert.Equal(
            "还没有保存 llm.example.com:8443 的凭据。各家的凭据分开保存，填好后点「保存凭据」。",
            form.KeyHint(null));
    }

    [Fact]
    public void When_only_the_scheme_differs_the_hint_spells_out_both_origins()
    {
        // http → https（或反过来）：主机名一样，只写主机会说成"已存了 x，却说 x 没有"。
        var saved = new AppSettings { BackendBaseUrl = "http://llm.example.com/v1" }.WithApiKey("sk-local");
        var form = new ServiceForm(saved, "https://llm.example.com/v1", "m", TypedKey: "");

        Assert.Equal(
            "还没有保存 https://llm.example.com 的凭据（已保存的是 http://llm.example.com）。",
            form.KeyHint(null));
    }

    [Fact]
    public void A_key_written_around_the_one_door_counts_as_nothing_saved()
    {
        // 绕过 WithApiKey 直接填旧字段：既拿不到密钥，也不会被当成"别家存过"。
        var unbound = new AppSettings { BackendApiKey = "sk-unbound", BackendBaseUrl = DeepSeek };
        var form = new ServiceForm(unbound, DeepSeek, "m", TypedKey: "");

        Assert.Equal(string.Empty, form.Key);
        Assert.Null(form.KeyHint("DeepSeek"));
    }

    [Fact]
    public void The_hint_stays_silent_when_the_key_fits_or_is_being_typed_or_there_is_none()
    {
        Assert.Null(new ServiceForm(SavedFor(DeepSeek), DeepSeek, "m", "").KeyHint("DeepSeek"));
        Assert.Null(new ServiceForm(SavedFor(DeepSeek), Zhipu, "m", "sk-typed").KeyHint("智谱"));
        Assert.Null(new ServiceForm(new AppSettings(), Zhipu, "m", "").KeyHint("智谱"));

        // 没有地址就谈不上"这家服务商"。
        Assert.Null(new ServiceForm(SavedFor(DeepSeek), "", "m", "").KeyHint(null));
    }
}

/// <summary>
/// 「测试连接」的第四种人话结果（票 29）：来源不一致、凭据框又留空——不发
/// 请求，直接说"请先填写这家服务商的密钥"。这不算失败。
/// </summary>
public class ConnectionProbeKeyOriginTests
{
    private const string DeepSeek = "https://api.deepseek.com";
    private const string Zhipu = "https://open.bigmodel.cn/api/paas/v4";

    private static AppSettings SavedFor(string baseUrl, string key = "sk-deepseek")
        => new AppSettings { BackendBaseUrl = baseUrl, BackendModel = "some-model" }.WithApiKey(key);

    private static ScriptedHandler Ok()
        => new(_ => BackendTransport.Stream("""{"choices":[{"message":{"content":"你好"}}]}""", "[DONE]"));

    [Fact]
    public async Task A_blank_box_over_another_providers_key_gets_the_fourth_answer_and_sends_nothing()
    {
        var handler = Ok();
        var form = new ServiceForm(SavedFor(DeepSeek), Zhipu, "glm-4-flash-250414", TypedKey: "");

        var outcome = await ConnectionProbe.TestAsync(
            form, ProviderPresets.Find("zhipu"), new HttpClient(handler));

        Assert.Equal(ConnectionTestVerdict.NeedsKey, outcome.Verdict);
        Assert.Contains("请先填写这家服务商的密钥", outcome.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_fourth_answer_is_not_one_of_the_failures()
    {
        var form = new ServiceForm(SavedFor(DeepSeek), Zhipu, "glm", TypedKey: "");

        var outcome = await ConnectionProbe.TestAsync(form, httpClient: new HttpClient(Ok()));

        Assert.NotEqual(ConnectionTestVerdict.InvalidKey, outcome.Verdict);
        Assert.NotEqual(ConnectionTestVerdict.Unreachable, outcome.Verdict);
    }

    [Fact]
    public async Task A_typed_key_is_tested_even_when_the_saved_one_belongs_to_another_provider()
    {
        var handler = Ok();
        var form = new ServiceForm(SavedFor(DeepSeek), Zhipu, "glm-4-flash-250414", TypedKey: "sk-zhipu");

        var outcome = await ConnectionProbe.TestAsync(
            form, ProviderPresets.Find("zhipu"), new HttpClient(handler));

        Assert.Equal(ConnectionTestVerdict.Success, outcome.Verdict);
        var request = handler.Requests.Single().Message;
        Assert.Equal("open.bigmodel.cn", request.RequestUri!.Host);
        Assert.Equal("sk-zhipu", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task The_saved_key_is_tested_when_the_box_is_blank_and_the_origin_matches()
    {
        var handler = Ok();
        var form = new ServiceForm(
            SavedFor(DeepSeek), "https://api.deepseek.com/v1", "deepseek-flash", TypedKey: "");

        var outcome = await ConnectionProbe.TestAsync(form, httpClient: new HttpClient(handler));

        Assert.Equal(ConnectionTestVerdict.Success, outcome.Verdict);
        Assert.Equal("sk-deepseek", handler.Requests.Single().Message.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task With_no_key_saved_anywhere_the_ordinary_refusal_still_applies()
    {
        var handler = Ok();
        var form = new ServiceForm(new AppSettings(), Zhipu, "glm", TypedKey: "");

        var outcome = await ConnectionProbe.TestAsync(form, httpClient: new HttpClient(handler));

        Assert.Equal(ConnectionTestVerdict.Unreachable, outcome.Verdict);
        Assert.NotEqual(ConnectionTestVerdict.NeedsKey, outcome.Verdict);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_form_missing_its_address_or_model_gets_the_ordinary_refusal_first()
    {
        var handler = Ok();

        var noModel = await ConnectionProbe.TestAsync(
            new ServiceForm(SavedFor(DeepSeek), Zhipu, "", TypedKey: ""), httpClient: new HttpClient(handler));
        var noAddress = await ConnectionProbe.TestAsync(
            new ServiceForm(SavedFor(DeepSeek), "", "glm", TypedKey: ""), httpClient: new HttpClient(handler));

        Assert.Equal(ConnectionTestVerdict.Unreachable, noModel.Verdict);
        Assert.Equal(ConnectionTestVerdict.Unreachable, noAddress.Verdict);
        Assert.Empty(handler.Requests);
    }
}
