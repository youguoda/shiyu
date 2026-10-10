using Shiyu.Core;

namespace Shiyu.Core.Tests;

// AppSettings.SecretProtector is ambient state: these classes must not run
// in parallel with anything that swaps the protector mid-save (a save under
// the fake followed by a load under null drops the key — a race, not a bug).
[Collection("settings-io")]
public class AppSettingsTests
{
    private sealed class TempFile : IDisposable
    {
        private readonly string _directory;

        public TempFile()
        {
            _directory = Path.Combine(Path.GetTempPath(), "shiyu-settings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path_ = Path.Combine(_directory, "settings.json");
        }

        public string Path_ { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void A_missing_file_gives_usable_defaults()
    {
        using var file = new TempFile();

        var settings = AppSettings.Load(file.Path_);

        Assert.Equal("Chinese", settings.TargetLanguage);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(30, settings.ImageRetentionDays);
        Assert.False(settings.Backend.IsConfigured);

        // The narrow bar: opened by quick paste, density knobs in the middle
        // of their range, and no remembered position yet.
        Assert.Equal("Ctrl+Shift+V", settings.QuickBarHotkey);
        Assert.Equal(4, settings.BarTextLines);
        Assert.Equal(120, settings.BarImageHeight);
        Assert.Equal(3, settings.BarFileCount);
        Assert.Null(settings.BarLeft);

        // 常驻钉住默认关（用户需求 2026-10-10）：贴完就走，点别处就收。
        Assert.False(settings.BarPinned);
    }

    [Fact]
    public void Fields_of_the_retired_resident_bar_load_as_nothing()
    {
        // 用户需求 2026-10-10 撤掉常驻窄条：老设置文件里的三个字段读进来什么也
        // 不是——不报错、不把用户送回默认，钉住也不因"保持在最前"开着就被
        // 打开（那是另一件事）。下一次保存时它们自然消失。
        using var file = new TempFile();
        File.WriteAllText(file.Path_, """
            {
              "TargetLanguage": "English",
              "BarHotkey": "Ctrl+Shift+B",
              "BarAtCursor": false,
              "BarAlwaysOnTop": true
            }
            """);

        var loaded = AppSettings.Load(file.Path_);
        Assert.Equal("English", loaded.TargetLanguage);
        Assert.False(loaded.BarPinned);

        loaded.Save(file.Path_);
        var saved = File.ReadAllText(file.Path_);
        Assert.DoesNotContain("\"BarHotkey\"", saved);
        Assert.DoesNotContain("\"BarAtCursor\"", saved);
        Assert.DoesNotContain("\"BarAlwaysOnTop\"", saved);
        Assert.Contains("\"BarPinned\": false", saved);
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        using var file = new TempFile();
        var original = new AppSettings
        {
            TargetLanguage = "English",
            SourceLanguage = "Chinese",
            BackendBaseUrl = "https://example.com/v1",
            BackendModel = "some-model",
            ImageRetentionDays = 7,
            StartWithWindows = false,
            Theme = AppTheme.Dark,
            QuickBarHotkey = "Ctrl+Alt+B",
            BarTextLines = 6,
            BarImageHeight = 200,
            BarFileCount = 5,
            BarPinned = true,
            BarLeft = 12.5,
            BarTop = 34.5,
            BarHeight = 800,
            ExclusionRules = [new StoredExclusionRule(ExclusionRuleKind.SourceApp, "MyVault")],
        }.WithApiKey("a-secret");

        original.Save(file.Path_);
        var loaded = AppSettings.Load(file.Path_);

        // Compared field by field rather than as whole records: the rule list
        // is an interface reference, which records compare by identity.
        Assert.Equal(original.TargetLanguage, loaded.TargetLanguage);
        Assert.Equal(original.SourceLanguage, loaded.SourceLanguage);
        Assert.Equal(original.BackendBaseUrl, loaded.BackendBaseUrl);
        Assert.Equal(original.BackendModel, loaded.BackendModel);
        Assert.Equal("a-secret", loaded.KeyFor(loaded.BackendBaseUrl));
        Assert.Equal(original.SavedProviders, loaded.SavedProviders);
        Assert.Equal(original.ImageRetentionDays, loaded.ImageRetentionDays);
        Assert.Equal(original.StartWithWindows, loaded.StartWithWindows);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal("Ctrl+Alt+B", loaded.QuickBarHotkey);
        Assert.Equal(6, loaded.BarTextLines);
        Assert.Equal(200, loaded.BarImageHeight);
        Assert.Equal(5, loaded.BarFileCount);
        Assert.True(loaded.BarPinned);
        Assert.Equal(12.5, loaded.BarLeft);
        Assert.Equal(34.5, loaded.BarTop);
        Assert.Equal(800, loaded.BarHeight);
        Assert.Equal(original.ExclusionRules, loaded.ExclusionRules);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_instead_of_stopping_startup()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path_, "{ this is not json");

        var settings = AppSettings.Load(file.Path_);

        // Shiyu has no window to show an error in; refusing to start would look
        // to the user like a tool that simply died.
        Assert.Equal("Chinese", settings.TargetLanguage);
    }

    [Fact]
    public void An_interrupted_save_leaves_the_previous_settings_intact()
    {
        using var file = new TempFile();
        new AppSettings { TargetLanguage = "English" }.Save(file.Path_);

        // A stale temporary file from a save that never finished must not be
        // mistaken for the real thing.
        File.WriteAllText(file.Path_ + ".tmp", "{ half written");

        Assert.Equal("English", AppSettings.Load(file.Path_).TargetLanguage);
    }

    [Fact]
    public void The_users_own_rules_are_added_to_the_shipped_presets_rather_than_replacing_them()
    {
        var settings = new AppSettings
        {
            ExclusionRules = [new StoredExclusionRule(ExclusionRuleKind.SourceApp, "MyVault")],
        };

        var policy = settings.BuildExclusionPolicy();

        // Losing the presets because the user added one rule of their own would
        // quietly reopen the hole they are there to close.
        Assert.Contains(policy.Rules, rule => rule.Value == "MyVault");
        Assert.Contains(policy.Rules, rule => rule.Value == "KeePassXC");
    }

    [Fact]
    public void Saving_creates_the_directory_when_it_is_not_there_yet()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shiyu-settings", Guid.NewGuid().ToString("N"), "nested");
        var path = Path.Combine(directory, "settings.json");

        try
        {
            new AppSettings().Save(path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true); }
            catch (IOException) { }
        }
    }

    // --- TryParse: the gate a settings restore has to pass (O-02) -----------

    [Fact]
    public void TryParse_accepts_a_saved_settings_file_and_upgrades_former_defaults()
    {
        using var file = new TempFile();
        var saved = new AppSettings { TargetLanguage = "Japanese", ImageRetentionDays = 14 };
        saved.Save(file.Path_);

        Assert.True(AppSettings.TryParse(File.ReadAllText(file.Path_), out var parsed));
        Assert.Equal("Japanese", parsed.TargetLanguage);
        Assert.Equal(14, parsed.ImageRetentionDays);

        // The action-list upgrade Load does belongs to parsing, not to the
        // file: a restore of an old default must not re-import the old list.
        var old = new AppSettings { BarActions = ["copy", "paste", "plain", "open", "locate", "pin", "delete"] };
        Assert.True(AppSettings.TryParse(
            System.Text.Json.JsonSerializer.Serialize(old), out var upgraded));
        Assert.Equal(HoverActions.All, upgraded.BarActions);
    }

    [Fact]
    public void The_content_size_defaults_to_standard_and_round_trips_by_name()
    {
        // 内容字号（2026-10-09）：没写这一项的设置文件就是原来的 18；存的是档名，不是数字。
        Assert.True(AppSettings.TryParse("{}", out var fresh));
        Assert.Equal(ContentFontSize.Standard, fresh.ContentFontSize);

        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { ContentFontSize = ContentFontSize.Larger });
        Assert.Contains("\"Larger\"", json);
        Assert.True(AppSettings.TryParse(json, out var larger));
        Assert.Equal(ContentFontSize.Larger, larger.ContentFontSize);
    }

    [Fact]
    public void A_saved_size_survives_the_new_level_in_front_of_it()
    {
        // 2026-10-10 在最前面插了「较小」：已存的档名照旧读回原来那一档，不会被挤到别处。
        Assert.True(AppSettings.TryParse("""{ "ContentFontSize": "Small" }""", out var small));
        Assert.Equal(ContentFontSize.Small, small.ContentFontSize);

        Assert.True(AppSettings.TryParse("""{ "ContentFontSize": "Smaller" }""", out var smaller));
        Assert.Equal(ContentFontSize.Smaller, smaller.ContentFontSize);
    }

    [Fact]
    public void The_auto_copy_switch_is_off_until_the_user_turns_it_on()
    {
        // 「自动复制译文」会改写用户的剪贴板：升级上来的设置文件没有这一项，必须读成关。
        Assert.True(AppSettings.TryParse("{}", out var fresh));
        Assert.False(fresh.AutoCopyTranslation);

        Assert.True(AppSettings.TryParse(
            System.Text.Json.JsonSerializer.Serialize(new AppSettings { AutoCopyTranslation = true }),
            out var on));
        Assert.True(on.AutoCopyTranslation);
    }

    [Fact]
    public void The_hover_preview_switch_defaults_off_and_round_trips()
    {
        // 用户需求 2026-10-09：预览弹窗默认关闭——没写这一项的设置文件读成关。
        Assert.True(AppSettings.TryParse("{}", out var fresh));
        Assert.False(fresh.PreviewOnHover);
        Assert.Equal(500, fresh.PreviewHoverDelayMs);

        Assert.True(AppSettings.TryParse("""{ "PreviewOnHover": true }""", out var on));
        Assert.True(on.PreviewOnHover);

        Assert.True(AppSettings.TryParse(
            System.Text.Json.JsonSerializer.Serialize(
                new AppSettings { PreviewOnHover = false, PreviewHoverDelayMs = 700 }),
            out var off));
        Assert.False(off.PreviewOnHover);
        Assert.Equal(700, off.PreviewHoverDelayMs);
    }

    [Fact]
    public void A_stored_zero_delay_comes_back_as_the_hover_switch_turned_off()
    {
        // Before the switch, "0" was how hover previews were turned off: the
        // choice survives as the switch, and the delay gets its default back
        // for the day the switch goes on again.
        Assert.True(AppSettings.TryParse("""{ "PreviewHoverDelayMs": 0 }""", out var migrated));
        Assert.False(migrated.PreviewOnHover);
        Assert.Equal(500, migrated.PreviewHoverDelayMs);
    }

    [Fact]
    public void TryParse_refuses_malformed_json_rather_than_defaulting_silently()
    {
        Assert.False(AppSettings.TryParse("{ this is not json", out var fromJunk));
        Assert.Null(fromJunk);

        // The restore path hinges on this difference from Load: Load quietly
        // falls back to defaults because startup must survive; TryParse says
        // no so the restore can keep the current settings instead.
        Assert.True(AppSettings.TryParse("{}", out var empty));
        Assert.Equal("Chinese", empty.TargetLanguage);
    }

    [Fact]
    public void TryParse_refuses_empty_and_null_input()
    {
        Assert.False(AppSettings.TryParse("", out var empty));
        Assert.Null(empty);

        Assert.False(AppSettings.TryParse("   ", out var blank));
        Assert.Null(blank);

        Assert.False(AppSettings.TryParse("null", out var explicitNull));
        Assert.Null(explicitNull);
    }

    [Fact]
    public void TryParse_fills_missing_fields_with_defaults()
    {
        Assert.True(AppSettings.TryParse("""{"TargetLanguage":"Japanese"}""", out var parsed));

        Assert.Equal("Japanese", parsed.TargetLanguage);
        Assert.True(parsed.StartWithWindows);
        Assert.Equal(30, parsed.ImageRetentionDays);
        Assert.Equal("Ctrl+Shift+Z", parsed.CaptureHotkey);
        Assert.Empty(parsed.ExclusionRules);
    }
}
