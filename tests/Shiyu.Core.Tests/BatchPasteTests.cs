using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 「粘贴全部」（评审 2026-10-16 F-12）：选中的条目按选中先后、一条一行贴出去；文件贴路径，图片跳过。
/// </summary>
public class BatchPasteTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static Entry Text(long id, string text) => new(id, text, "test", Noon);

    [Fact]
    public void Three_entries_paste_as_three_lines_in_the_given_order()
    {
        var batch = BatchPaste.Join([Text(3, "third"), Text(1, "first"), Text(2, "second")]);

        Assert.Equal("third\r\nfirst\r\nsecond", batch.Text);
        Assert.Equal(3, batch.Pasted);
        Assert.Equal(0, batch.SkippedImages);
    }

    [Fact]
    public void A_trailing_line_break_does_not_turn_into_a_blank_line()
    {
        var batch = BatchPaste.Join([Text(1, "one\r\n"), Text(2, "two\n")]);

        Assert.Equal("one\r\ntwo", batch.Text);
    }

    [Fact]
    public void A_multi_line_entry_keeps_its_own_lines()
    {
        var batch = BatchPaste.Join([Text(1, "a\r\nb"), Text(2, "c")]);

        Assert.Equal("a\r\nb\r\nc", batch.Text);
    }

    [Fact]
    public void Files_paste_their_paths_and_images_are_skipped_and_counted()
    {
        var files = new Entry(2, "a.txt 等 2 项", "test", Noon)
        {
            Kind = EntryKind.Files,
            Files = [@"C:\docs\a.txt", @"C:\docs\b.txt"],
        };
        var image = new Entry(3, "图片 10×10", "test", Noon) { Kind = EntryKind.Image };

        var batch = BatchPaste.Join([Text(1, "note"), files, image]);

        Assert.Equal("note\r\nC:\\docs\\a.txt\r\nC:\\docs\\b.txt", batch.Text);
        Assert.Equal(2, batch.Pasted);
        Assert.Equal(1, batch.SkippedImages);
    }

    [Fact]
    public void Only_images_leave_nothing_to_paste()
    {
        var image = new Entry(1, "图片 10×10", "test", Noon) { Kind = EntryKind.Image };

        var batch = BatchPaste.Join([image]);

        Assert.Equal(string.Empty, batch.Text);
        Assert.Equal(0, batch.Pasted);
        Assert.Equal(1, batch.SkippedImages);
    }

    // ── 选中的先后 ──

    [Fact]
    public void Ctrl_clicks_are_remembered_in_the_order_they_happened()
    {
        var order = new SelectionOrder();
        order.Add([30]);
        order.Add([10]);
        order.Add([20]);

        // 列表控件报上来的是列表顺序；排出来的是点选的先后。
        Assert.Equal([30, 10, 20], order.Arrange([10, 20, 30]));
    }

    [Fact]
    public void Deselecting_drops_an_entry_and_selecting_it_again_puts_it_last()
    {
        var order = new SelectionOrder();
        order.Add([1, 2, 3]);
        order.Remove([1]);
        order.Add([1]);

        Assert.Equal([2, 3, 1], order.Arrange([1, 2, 3]));
    }

    [Fact]
    public void Entries_selected_while_recording_was_held_follow_in_list_order()
    {
        var order = new SelectionOrder();
        order.Add([5]);

        // 5 是点选的；7、6 是重载补回时出现的、没记到先后的。
        Assert.Equal([5, 6, 7], order.Arrange([6, 5, 7]));
    }

    [Fact]
    public void Remembered_entries_that_are_no_longer_selected_are_forgotten()
    {
        var order = new SelectionOrder();
        order.Add([1, 2]);

        Assert.Equal([2], order.Arrange([2]));

        // 忘掉之后再选上，排在后面而不是回到原来的位置。
        order.Add([1]);
        Assert.Equal([2, 1], order.Arrange([1, 2]));
    }
}
