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

        // The narrow bar: a hotkey of its own, density knobs in the middle of
        // their range, and no remembered position yet.
        Assert.Equal("Ctrl+Shift+B", settings.BarHotkey);
        Assert.Equal(4, settings.BarTextLines);
        Assert.Equal(120, settings.BarImageHeight);
        Assert.Equal(3, settings.BarFileCount);
        Assert.Null(settings.BarLeft);

        // Pinning to the top of the z-order is the resident bar's working
        // posture; turning it off is a deliberate act (ticket 39).
        Assert.True(settings.BarAlwaysOnTop);
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
            BackendApiKey = "a-secret",
            ImageRetentionDays = 7,
            StartWithWindows = false,
            Theme = AppTheme.Dark,
            BarHotkey = "Ctrl+Alt+B",
            BarTextLines = 6,
            BarImageHeight = 200,
            BarFileCount = 5,
            BarAlwaysOnTop = false,
            BarLeft = 12.5,
            BarTop = 34.5,
            BarHeight = 800,
            ExclusionRules = [new StoredExclusionRule(ExclusionRuleKind.SourceApp, "MyVault")],
        };

        original.Save(file.Path_);
        var loaded = AppSettings.Load(file.Path_);

        // Compared field by field rather than as whole records: the rule list
        // is an interface reference, which records compare by identity.
        Assert.Equal(original.TargetLanguage, loaded.TargetLanguage);
        Assert.Equal(original.SourceLanguage, loaded.SourceLanguage);
        Assert.Equal(original.BackendBaseUrl, loaded.BackendBaseUrl);
        Assert.Equal(original.BackendModel, loaded.BackendModel);
        Assert.Equal(original.BackendApiKey, loaded.BackendApiKey);
        Assert.Equal(original.ImageRetentionDays, loaded.ImageRetentionDays);
        Assert.Equal(original.StartWithWindows, loaded.StartWithWindows);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal("Ctrl+Alt+B", loaded.BarHotkey);
        Assert.Equal(6, loaded.BarTextLines);
        Assert.Equal(200, loaded.BarImageHeight);
        Assert.Equal(5, loaded.BarFileCount);
        Assert.False(loaded.BarAlwaysOnTop);
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
