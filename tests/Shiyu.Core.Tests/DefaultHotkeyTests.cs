using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 默认全局键换成 Win+Alt 组（评审 2026-10-16 F-04）：原来的 Ctrl+Shift+Z / X / V 与 Alt+Q 盖掉的是编辑器
/// 的"重做"、浏览器与聊天软件的"粘贴为纯文本"、Microsoft 365 的"跳到搜索框"。只对新安装生效——老用户的
/// 设置文件里缺哪一项，读回来仍是那一项当年的默认。
/// </summary>
public class DefaultHotkeyTests
{
    [Fact]
    public void A_new_install_gets_the_win_alt_set()
    {
        var settings = new AppSettings();

        Assert.Equal("Win+Alt+Z", settings.CaptureHotkey);
        Assert.Equal("Win+Alt+X", settings.ClipboardTranslateHotkey);
        Assert.Equal("Win+Alt+V", settings.QuickBarHotkey);
        Assert.Equal("Win+Alt+Q", settings.ReverseInputHotkey);
        Assert.Equal(string.Empty, settings.LibraryHotkey);
    }

    [Fact]
    public void The_new_defaults_parse_collide_with_nothing_and_are_written_the_canonical_way()
    {
        var settings = new AppSettings();

        Assert.Empty(HotkeyPlan.Build(settings).Problems);
        foreach (var hotkey in new[]
                 {
                     settings.CaptureHotkey, settings.ClipboardTranslateHotkey,
                     settings.QuickBarHotkey, settings.ReverseInputHotkey,
                 })
        {
            Assert.Equal(hotkey, HotkeySpec.Parse(hotkey)!.ToString());
            Assert.Null(HotkeyConflicts.UseElsewhere(hotkey));
        }
    }

    [Fact]
    public void An_old_settings_file_without_the_hotkeys_keeps_the_keys_it_has_been_using()
    {
        Assert.True(AppSettings.TryParse("""{ "TargetLanguage": "Chinese" }""", out var parsed));

        Assert.Equal("Ctrl+Shift+Z", parsed.CaptureHotkey);
        Assert.Equal("Ctrl+Shift+X", parsed.ClipboardTranslateHotkey);
        Assert.Equal("Ctrl+Shift+V", parsed.QuickBarHotkey);
        Assert.Equal("Alt+Q", parsed.ReverseInputHotkey);
    }

    [Fact]
    public void Only_the_missing_ones_fall_back()
    {
        // 0.10 以前存下的文件有前三个、没有反向输入：前三个照写的，反向输入补回它当年的 Alt+Q。
        Assert.True(AppSettings.TryParse(
            """{ "CaptureHotkey": "Ctrl+Alt+T", "ClipboardTranslateHotkey": "Win+Alt+X", "QuickBarHotkey": "" }""",
            out var parsed));

        Assert.Equal("Ctrl+Alt+T", parsed.CaptureHotkey);
        Assert.Equal("Win+Alt+X", parsed.ClipboardTranslateHotkey);
        Assert.Equal(string.Empty, parsed.QuickBarHotkey);
        Assert.Equal("Alt+Q", parsed.ReverseInputHotkey);
    }

    [Fact]
    public void A_new_install_that_saved_once_stays_on_the_new_set()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shiyu-hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings().Save(path);

            var loaded = AppSettings.Load(path);

            Assert.Equal("Win+Alt+Z", loaded.CaptureHotkey);
            Assert.Equal("Win+Alt+Q", loaded.ReverseInputHotkey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Ctrl+Shift+V", "粘贴为纯文本")]
    [InlineData("shift+ctrl+v", "粘贴为纯文本")]
    [InlineData("Ctrl+Shift+Z", "重做")]
    [InlineData("Alt+Q", "跳到搜索框")]
    [InlineData("Ctrl+Alt+V", "选择性粘贴")]
    [InlineData("Ctrl+Alt+Z", "提取消息")]
    public void Known_clashes_are_named_however_they_are_written(string hotkey, string use)
    {
        Assert.Contains(use, HotkeyConflicts.UseElsewhere(hotkey));
        Assert.Contains(use, HotkeyConflicts.Warning(hotkey));
    }

    [Theory]
    [InlineData("Ctrl+Alt+T")]
    [InlineData("Win+Alt+Z")]
    [InlineData("")]
    [InlineData("not a hotkey")]
    public void Anything_else_gets_no_warning(string hotkey)
        => Assert.Null(HotkeyConflicts.Warning(hotkey));

    [Fact]
    public void Win_is_written_first_the_way_windows_writes_its_shortcuts()
        => Assert.Equal("Win+Ctrl+Shift+Alt+K", HotkeySpec.Parse("Alt+Shift+Ctrl+Win+K")!.ToString());
}
