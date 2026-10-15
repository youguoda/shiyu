using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 划词徽章的过滤链（票 37），链上每一环都要有测试钉住：
/// 总开关 → 前台桌面早退（取词前）→ 最小长度 → 须含字母 → 拒绝路径形（取词后）。
/// 链在两个时刻分两段执行——桌面早退必须在模拟 Ctrl+C 之前把人拦下，
/// 文字三环只在取到文字后才有意义。
/// </summary>
public class SelectionBadgeFilterTests
{
    // --- 取词之前：总开关 → 桌面早退 ---

    [Fact]
    public void The_master_switch_gates_everything_even_the_desktop_check()
    {
        // 总开关排在链首：关着的时候连"前台是什么"都不必问。
        Assert.Equal(
            SelectionBadgeVerdict.Disabled,
            SelectionBadgeFilter.JudgeBeforeCapture(enabled: false, desktopForeground: true, overlayForeground: true));
    }

    [Fact]
    public void The_desktop_foreground_is_an_early_exit()
    {
        // 桌面上 Ctrl+C 只会复制剪贴板里原有的东西——模拟按键毫无意义，
        // 这道闸必须在取词之前拦下，而不是取完词再扔掉。
        Assert.Equal(
            SelectionBadgeVerdict.DesktopShell,
            SelectionBadgeFilter.JudgeBeforeCapture(enabled: true, desktopForeground: true, overlayForeground: false));
    }

    [Fact]
    public void A_full_screen_overlay_in_front_is_an_early_exit()
    {
        // 用户日志 2026-10-14：截图工具的框选蒙版里拖出一个框，收到 Ctrl+C 就把选区复制走、当场
        // 收工——还没标注，紧接着的还原又把截图换掉。不发按键，不借剪贴板。
        Assert.Equal(
            SelectionBadgeVerdict.FullScreenOverlay,
            SelectionBadgeFilter.JudgeBeforeCapture(enabled: true, desktopForeground: false, overlayForeground: true));
    }

    [Fact]
    public void An_ordinary_foreground_proceeds_to_capture()
        => Assert.Equal(
            SelectionBadgeVerdict.Offer,
            SelectionBadgeFilter.JudgeBeforeCapture(enabled: true, desktopForeground: false, overlayForeground: false));

    // --- 取词之后：最小长度 → 须含字母 → 拒绝路径形 ---

    [Fact]
    public void A_single_character_is_too_short()
        => Assert.Equal(SelectionBadgeVerdict.TooShort, SelectionBadgeFilter.JudgeCapturedText("a"));

    [Fact]
    public void Whitespace_does_not_count_toward_the_length()
        => Assert.Equal(
            SelectionBadgeVerdict.TooShort, SelectionBadgeFilter.JudgeCapturedText("  a  "));

    [Fact]
    public void Two_characters_are_enough()
        => Assert.Equal(SelectionBadgeVerdict.Offer, SelectionBadgeFilter.JudgeCapturedText("hi"));

    [Fact]
    public void Bare_numbers_and_punctuation_have_nothing_to_translate()
    {
        Assert.Equal(SelectionBadgeVerdict.NoLetters, SelectionBadgeFilter.JudgeCapturedText("123 456"));
        Assert.Equal(SelectionBadgeVerdict.NoLetters, SelectionBadgeFilter.JudgeCapturedText("!?..."));
    }

    [Fact]
    public void Chinese_characters_count_as_letters()
        => Assert.Equal(SelectionBadgeVerdict.Offer, SelectionBadgeFilter.JudgeCapturedText("今天"));

    [Fact]
    public void Windows_drive_paths_are_rejected()
        => Assert.Equal(
            SelectionBadgeVerdict.PathLike,
            SelectionBadgeFilter.JudgeCapturedText(@"C:\Users\me\notes.txt"));

    [Fact]
    public void Unc_paths_are_rejected()
        => Assert.Equal(
            SelectionBadgeVerdict.PathLike,
            SelectionBadgeFilter.JudgeCapturedText(@"\\server\share\report.pdf"));

    [Fact]
    public void Posix_paths_are_rejected()
        => Assert.Equal(
            SelectionBadgeVerdict.PathLike,
            SelectionBadgeFilter.JudgeCapturedText("/usr/local/bin/dotnet"));

    [Fact]
    public void The_length_gate_runs_before_the_letter_gate()
        => Assert.Equal(
            SelectionBadgeVerdict.TooShort, SelectionBadgeFilter.JudgeCapturedText("1"));

    [Fact]
    public void The_letter_gate_runs_before_the_path_gate()
    {
        // "C:" 有字母、不构成路径形——两环各管各的，钉住先后不互相吞。
        Assert.Equal(SelectionBadgeVerdict.Offer, SelectionBadgeFilter.JudgeCapturedText("C:"));

        // 长度够、含字母、且是路径形：走到第三环才被拒。
        Assert.Equal(SelectionBadgeVerdict.PathLike, SelectionBadgeFilter.JudgeCapturedText("C:\\a"));
    }

    [Fact]
    public void An_ordinary_sentence_is_offered()
        => Assert.Equal(
            SelectionBadgeVerdict.Offer,
            SelectionBadgeFilter.JudgeCapturedText("The quick brown fox jumps over the lazy dog."));
}
