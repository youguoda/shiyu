using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 反向输入框的摆放与长高（票 43）：默认贴在插入符下方，下方空间不够时翻到上方；翻到上方后保持底边
/// 贴着锚点、向上长（y + 旧高 − 新高）。翻转与钳制复用 <see cref="BadgePlacement"/>。
/// 全部是物理像素。
/// </summary>
public class ReverseInputPlacementTests
{
    private static readonly ScreenRect Work = new(0, 0, 1920, 1040);
    private const int Width = 520;
    private const int Gap = 20;

    [Fact]
    public void It_sits_below_the_caret_when_there_is_room()
    {
        var placement = ReverseInputPlacement.Place(new ScreenPoint(500, 300), Width, 120, Work, Gap);

        Assert.False(placement.Above);
        Assert.Equal(new ScreenPoint(520, 320), placement.PositionFor(120, Work));
    }

    [Fact]
    public void It_flips_above_the_caret_when_there_is_no_room_below()
    {
        var placement = ReverseInputPlacement.Place(new ScreenPoint(500, 1000), Width, 120, Work, Gap);

        Assert.True(placement.Above);

        // 底边在锚点上方一个间隙处，窗口整个落在锚点之上。
        var position = placement.PositionFor(120, Work);
        Assert.Equal(1000 - Gap, position.Y + 120);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(500, 300)]
    [InlineData(1500, 700)]
    [InlineData(1900, 1030)]
    [InlineData(10, 1035)]
    [InlineData(960, 900)]
    [InlineData(960, 921)]
    [InlineData(960, 920)]
    public void The_first_position_is_exactly_what_the_badge_placement_gives(int x, int y)
    {
        // "翻转与钳制复用 BadgePlacement"：对一片锚点网格，位置逐点一致，翻转的判定也一致。
        var anchor = new ScreenPoint(x, y);
        var expected = BadgePlacement.Place(anchor, Width, 120, Work, Gap);

        var placement = ReverseInputPlacement.Place(anchor, Width, 120, Work, Gap);

        Assert.Equal(expected, placement.PositionFor(120, Work));
        Assert.Equal(expected.Y < anchor.Y, placement.Above);
    }

    [Fact]
    public void Growing_while_above_keeps_the_bottom_edge_and_grows_upwards()
    {
        var placement = ReverseInputPlacement.Place(new ScreenPoint(500, 1000), Width, 120, Work, Gap);
        var before = placement.PositionFor(120, Work);

        var after = placement.PositionFor(300, Work);

        // y + 旧高 − 新高：底边（y + 高）不动，顶边往上走。
        Assert.Equal(before.Y + 120 - 300, after.Y);
        Assert.Equal(before.Y + 120, after.Y + 300);
        Assert.Equal(before.X, after.X);
    }

    [Fact]
    public void Shrinking_while_above_comes_back_down_to_the_same_bottom_edge()
    {
        var placement = ReverseInputPlacement.Place(new ScreenPoint(500, 1000), Width, 300, Work, Gap);

        var small = placement.PositionFor(100, Work);
        var big = placement.PositionFor(300, Work);

        Assert.Equal(big.Y + 300, small.Y + 100);
    }

    [Fact]
    public void Growing_while_below_keeps_the_top_edge()
    {
        var placement = ReverseInputPlacement.Place(new ScreenPoint(500, 300), Width, 120, Work, Gap);

        Assert.Equal(placement.PositionFor(120, Work).Y, placement.PositionFor(300, Work).Y);
    }

    [Fact]
    public void Growing_below_past_the_bottom_of_the_screen_is_pushed_back_in()
    {
        var placement = ReverseInputPlacement.Place(new ScreenPoint(500, 800), Width, 120, Work, Gap);
        Assert.False(placement.Above);

        var grown = placement.PositionFor(400, Work);

        Assert.Equal(Work.Bottom, grown.Y + 400);
    }

    [Fact]
    public void Growing_above_past_the_top_of_the_screen_is_held_at_the_top()
    {
        var small = new ScreenRect(0, 0, 1920, 300);
        var placement = ReverseInputPlacement.Place(new ScreenPoint(500, 150), Width, 200, small, Gap);
        Assert.True(placement.Above);

        Assert.Equal(0, placement.PositionFor(250, small).Y);
    }

    [Fact]
    public void Dragging_the_height_back_after_a_clamp_returns_to_the_ideal_edge()
    {
        // 底边是"理想位置"而不是上一次被钳制后的结果：钳过一次，缩回去仍回到原来的底边。
        var placement = ReverseInputPlacement.Place(new ScreenPoint(500, 300), Width, 100, new ScreenRect(0, 0, 1920, 400), Gap);
        Assert.True(placement.Above);
        var work = new ScreenRect(0, 0, 1920, 400);

        var clamped = placement.PositionFor(390, work);
        var back = placement.PositionFor(100, work);

        Assert.Equal(0, clamped.Y);
        Assert.Equal(300 - Gap, back.Y + 100);
    }

    [Fact]
    public void It_flips_to_the_left_of_the_caret_near_the_right_edge_and_stays_on_screen()
    {
        var placement = ReverseInputPlacement.Place(new ScreenPoint(1850, 300), Width, 120, Work, Gap);

        var position = placement.PositionFor(120, Work);

        Assert.True(position.X + Width <= Work.Right);
        Assert.True(position.X >= Work.Left);
    }

    // --- 整窗高度 80–800 ----------------------------------------------------------------

    [Theory]
    [InlineData(20, 1000, 80)]
    [InlineData(80, 1000, 80)]
    [InlineData(300, 1000, 300)]
    [InlineData(800, 1000, 800)]
    [InlineData(2000, 1000, 800)]
    public void The_window_height_is_clamped_to_80_through_800(double height, double workArea, double expected)
        => Assert.Equal(expected, ReverseInputPlacement.ClampHeightDip(height, workArea));

    [Fact]
    public void On_a_short_screen_the_cap_follows_the_work_area_not_the_800()
    {
        // 工作区只有 500 DIP 高：留 16 的余量，上限是 484，而不是 800。
        Assert.Equal(484, ReverseInputPlacement.ClampHeightDip(2000, 500));
    }

    [Fact]
    public void A_tiny_work_area_never_pushes_the_cap_below_the_floor()
        => Assert.Equal(80, ReverseInputPlacement.ClampHeightDip(300, 60));

    // --- 拖过之后（用户需求 2026-10-09） -------------------------------------------------

    [Fact]
    public void A_dragged_box_grows_down_from_where_it_was_dropped()
    {
        // 原本翻在插入符上方、向上长；拖走之后不再追着那个锚点。
        var dragged = ReverseInputPlacement.Dragged(new ScreenPoint(900, 200));

        Assert.Equal(new ScreenPoint(900, 200), dragged.PositionFor(120, Work));
        Assert.Equal(new ScreenPoint(900, 200), dragged.PositionFor(400, Work));
    }

    [Fact]
    public void A_box_dropped_low_is_pushed_up_as_it_grows_and_returns_when_it_shrinks()
    {
        var dragged = ReverseInputPlacement.Dragged(new ScreenPoint(900, 900));

        Assert.Equal(1040 - 400, dragged.PositionFor(400, Work).Y);
        Assert.Equal(900, dragged.PositionFor(120, Work).Y);
    }
}
