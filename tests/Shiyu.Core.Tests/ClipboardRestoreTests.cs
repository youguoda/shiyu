using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 反向输入框回贴之后"要不要还原剪贴板"的判定（票 43）：窄条粘贴不还原，因为把内容留在剪贴板上是
/// 剪贴板管理器的本分；而反向输入框的结果只是"运输"，不该占掉用户原来的剪贴板。还原要谨慎：
/// 带排除标记的不还（写回时标记会丢，密码会以没有标记的形式重新出现）、有写不回去的格式不还、
/// 用户在这期间复制了别的东西就不去覆盖它——后者按剪贴板序列号判，比文本比对准。
/// </summary>
public class ClipboardRestoreTests
{
    private static ClipboardBackup TextOnly(string text = "原来的内容")
        => new(text, Html: null, Rtf: null, Files: [], Excluded: false, LostFormats: [], Empty: false);

    // --- 判定 --------------------------------------------------------------------------

    [Fact]
    public void An_untouched_clipboard_is_restored()
        => Assert.Equal(RestoreVerdict.Restore, ClipboardRestore.Decide(TextOnly(), 41, 41));

    [Fact]
    public void Text_html_and_rtf_together_are_restored()
    {
        var rich = TextOnly() with { Html = "<b>x</b>", Rtf = @"{\rtf1 x}" };

        Assert.Equal(RestoreVerdict.Restore, ClipboardRestore.Decide(rich, 7, 7));
    }

    [Fact]
    public void A_file_list_is_restored()
    {
        var files = new ClipboardBackup(null, null, null, [@"C:\a.txt", @"C:\b.txt"], false, [], false);

        Assert.Equal(RestoreVerdict.Restore, ClipboardRestore.Decide(files, 7, 7));
    }

    [Fact]
    public void An_empty_clipboard_is_restored_as_empty()
    {
        var empty = new ClipboardBackup(null, null, null, [], false, [], Empty: true);

        Assert.Equal(RestoreVerdict.Restore, ClipboardRestore.Decide(empty, 7, 7));
    }

    [Fact]
    public void A_clipboard_the_user_changed_in_the_meantime_is_left_alone()
    {
        // 写入后记下序列号，400ms 后序列号变了 = 用户（或别的程序）复制了别的东西。
        Assert.Equal(
            RestoreVerdict.SkipChangedSinceWrite, ClipboardRestore.Decide(TextOnly(), 41, 42));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(5u, 4u)]
    [InlineData(uint.MaxValue, 0u)]
    public void Any_difference_in_the_sequence_number_counts_as_a_change(uint written, uint now)
        => Assert.Equal(
            RestoreVerdict.SkipChangedSinceWrite, ClipboardRestore.Decide(TextOnly(), written, now));

    [Fact]
    public void A_snapshot_with_an_exclusion_marker_is_never_restored()
    {
        // 写回时标记会丢，密码就会以没有标记的形式重新出现在剪贴板上，被 Win+V 历史之类的工具记下来。
        var password = TextOnly("hunter2") with { Excluded = true };

        Assert.Equal(RestoreVerdict.SkipExcluded, ClipboardRestore.Decide(password, 3, 3));
    }

    [Fact]
    public void A_snapshot_holding_an_image_is_not_restored_because_it_cannot_be_written_back()
    {
        // 已知局限：剪贴板上会留下译文，原来的图片仍可在历史里找到。
        var picture = TextOnly() with { LostFormats = ["CF_DIB"] };

        Assert.Equal(RestoreVerdict.SkipUnrestorableFormat, ClipboardRestore.Decide(picture, 3, 3));
    }

    [Fact]
    public void A_snapshot_with_formats_but_nothing_writable_is_not_restored()
    {
        // 只有 HTML 没有文本：WindowsClipboardWriter 写不回去（写 HTML 要带着纯文本）。
        var htmlOnly = new ClipboardBackup(null, "<b>x</b>", null, [], false, [], Empty: false);

        Assert.Equal(RestoreVerdict.SkipUnrestorableFormat, ClipboardRestore.Decide(htmlOnly, 3, 3));
    }

    [Fact]
    public void The_exclusion_marker_outranks_everything_else_in_the_verdict()
    {
        // 带标记又有图片、序列号也变了：说"排除"——最要紧的那个理由。
        var everything = TextOnly() with { Excluded = true, LostFormats = ["CF_DIB"] };

        Assert.Equal(RestoreVerdict.SkipExcluded, ClipboardRestore.Decide(everything, 1, 2));
    }

    [Fact]
    public void The_restore_waits_400_milliseconds()
        => Assert.Equal(TimeSpan.FromMilliseconds(400), ReversePaste.RestoreDelay);

    // --- 格式分类 ----------------------------------------------------------------------

    [Theory]
    [InlineData(13u, null, ClipboardFormatRole.Restored)]          // CF_UNICODETEXT
    [InlineData(15u, null, ClipboardFormatRole.Restored)]          // CF_HDROP
    [InlineData(0xC001u, "HTML Format", ClipboardFormatRole.Restored)]
    [InlineData(0xC002u, "Rich Text Format", ClipboardFormatRole.Restored)]
    [InlineData(1u, null, ClipboardFormatRole.Synthesized)]        // CF_TEXT
    [InlineData(7u, null, ClipboardFormatRole.Synthesized)]        // CF_OEMTEXT
    [InlineData(16u, null, ClipboardFormatRole.Synthesized)]       // CF_LOCALE
    [InlineData(0xC003u, "ExcludeClipboardContentFromMonitorProcessing", ClipboardFormatRole.ExclusionMarker)]
    [InlineData(0xC004u, "CanIncludeInClipboardHistory", ClipboardFormatRole.ExclusionMarker)]
    [InlineData(2u, null, ClipboardFormatRole.Lost)]               // CF_BITMAP
    [InlineData(3u, null, ClipboardFormatRole.Lost)]               // CF_METAFILEPICT
    [InlineData(8u, null, ClipboardFormatRole.Lost)]               // CF_DIB
    [InlineData(14u, null, ClipboardFormatRole.Lost)]              // CF_ENHMETAFILE
    [InlineData(17u, null, ClipboardFormatRole.Lost)]              // CF_DIBV5
    [InlineData(12u, null, ClipboardFormatRole.Lost)]              // CF_WAVE
    [InlineData(0xC005u, "PNG", ClipboardFormatRole.Lost)]
    [InlineData(0xC006u, "FileGroupDescriptorW", ClipboardFormatRole.Lost)]
    [InlineData(0xC007u, "FileContents", ClipboardFormatRole.Lost)]
    [InlineData(0xC008u, "Chromium internal source URL", ClipboardFormatRole.Incidental)]
    [InlineData(0xC009u, "Shell IDList Array", ClipboardFormatRole.Incidental)]
    [InlineData(0xC00Au, "Preferred DropEffect", ClipboardFormatRole.Incidental)]
    [InlineData(0xC00Bu, "CanUploadToCloudClipboard", ClipboardFormatRole.Incidental)]
    public void Formats_fall_into_the_role_that_decides_how_they_count(
        uint format, string? name, ClipboardFormatRole expected)
        => Assert.Equal(expected, ClipboardFormats.Classify(format, name));

    [Theory]
    [InlineData("html format")]
    [InlineData("RICH TEXT FORMAT")]
    public void Registered_names_are_matched_without_regard_to_case(string name)
        => Assert.Equal(ClipboardFormatRole.Restored, ClipboardFormats.Classify(0xC100, name));

    [Fact]
    public void A_copy_from_a_browser_loses_nothing()
    {
        var lost = ClipboardFormats.LostAmong(
        [
            (13, null), (1, null), (7, null), (16, null),
            (0xC001, "HTML Format"), (0xC008, "Chromium internal source URL"),
        ]);

        Assert.Empty(lost);
    }

    [Fact]
    public void A_copy_of_files_from_the_explorer_loses_nothing()
    {
        var lost = ClipboardFormats.LostAmong(
        [
            (15, null), (0xC009, "Shell IDList Array"), (0xC00A, "Preferred DropEffect"),
            (0xC00C, "Shell Object Offsets"), (0xC00D, "FileNameW"), (0xC00E, "FileName"),
        ]);

        Assert.Empty(lost);
    }

    [Fact]
    public void A_screenshot_loses_its_pixels_and_says_which_formats()
    {
        var lost = ClipboardFormats.LostAmong([(8, null), (17, null), (2, null), (0xC005, "PNG")]);

        Assert.Equal(["CF_DIB", "CF_DIBV5", "CF_BITMAP", "PNG"], lost);
    }

    [Fact]
    public void Cells_copied_from_a_spreadsheet_carry_a_picture_and_so_are_not_restorable()
    {
        var lost = ClipboardFormats.LostAmong(
        [
            (13, null), (1, null), (0xC001, "HTML Format"), (0xC002, "Rich Text Format"),
            (0xC00F, "Biff12"), (14, null), (2, null),
        ]);

        Assert.Equal(["CF_ENHMETAFILE", "CF_BITMAP"], lost);
    }

    [Fact]
    public void An_attachment_dragged_out_of_a_mail_client_is_virtual_files_and_not_restorable()
    {
        var lost = ClipboardFormats.LostAmong([(0xC006, "FileGroupDescriptorW"), (0xC007, "FileContents")]);

        Assert.Equal(["FileGroupDescriptorW", "FileContents"], lost);
    }

    [Fact]
    public void The_exclusion_marker_names_are_the_ones_the_clipboard_monitor_honours()
    {
        Assert.True(ClipboardFormats.IsExclusionMarker("ExcludeClipboardContentFromMonitorProcessing"));
        Assert.True(ClipboardFormats.IsExclusionMarker("canincludeinclipboardhistory"));
        Assert.False(ClipboardFormats.IsExclusionMarker("HTML Format"));
        Assert.False(ClipboardFormats.IsExclusionMarker(null));
    }

    [Theory]
    [InlineData(1u, false)]  // DROPEFFECT_COPY
    [InlineData(4u, false)]  // DROPEFFECT_LINK
    [InlineData(5u, false)]  // COPY | LINK
    [InlineData(2u, true)]   // DROPEFFECT_MOVE：剪切。只写回文件列表会把"移动"悄悄变成"复制"
    [InlineData(3u, true)]
    [InlineData(0u, false)]
    public void A_cut_is_recognised_from_the_preferred_drop_effect(uint effect, bool isMove)
        => Assert.Equal(isMove, ClipboardFormats.IsMoveDropEffect(effect));
}
