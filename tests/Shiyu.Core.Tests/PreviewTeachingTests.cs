using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class PreviewTeachingTests
{
    private static readonly EntryKind[] Kinds = [EntryKind.Text, EntryKind.Image, EntryKind.Files];

    [Fact]
    public void Every_kind_teaches_double_click_enter_and_drag()
    {
        foreach (var kind in Kinds)
        {
            var keys = PreviewTeaching.For(kind).Select(step => step.Key).ToList();
            Assert.Equal(["双击", KeyMap.BadgeText("paste")!, "拖出"], keys);
        }
    }

    [Fact]
    public void The_enter_cap_comes_from_the_key_map()
    {
        // 键位即数据：粘贴键改了，教学帽跟着改，不另写一份。
        Assert.Equal("Enter", KeyMap.BadgeText("paste"));
    }

    [Fact]
    public void The_drag_step_names_only_the_cards_own_destination()
    {
        var destinations = Kinds.Select(kind => PreviewTeaching.For(kind)[^1].Action).ToList();

        Assert.Equal(Kinds.Length, destinations.Distinct().Count());
        Assert.Contains("编辑器", destinations[0]);
        Assert.Contains("聊天", destinations[1]);
        Assert.Contains("资源管理器", destinations[2]);
    }

    [Fact]
    public void Each_action_is_a_short_phrase_not_a_sentence()
    {
        // The row has to sit on one line under the panel's widest content:
        // short phrases, no run-on clauses (the old row was one sentence that
        // wrapped onto a second line and broke inside its brackets).
        foreach (var kind in Kinds)
        {
            Assert.All(PreviewTeaching.For(kind), step =>
            {
                Assert.True(step.Action.Length <= 10, step.Action);
                Assert.DoesNotContain("；", step.Action);
            });
        }
    }
}
