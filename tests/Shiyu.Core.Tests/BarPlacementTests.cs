using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>用户需求 2026-10-09：窄条的左上角应该就在光标处。</summary>
public class BarPlacementTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);

    private const int Width = 384;
    private const int Height = 620;

    [Fact]
    public void With_room_the_top_left_corner_is_the_pointer_tip()
    {
        var placed = BarPlacement.Place(BarPlacement.Pointer(new ScreenPoint(300, 200)), Width, Height, Screen);

        Assert.Equal(new ScreenPoint(300, 200), placed);
    }

    [Fact]
    public void Below_a_text_caret_the_bar_hangs_from_its_bottom_left()
    {
        // The caret line runs 200..220: the bar starts right under it, at its x.
        var caret = new ScreenRect(500, 200, 500, 220);

        Assert.Equal(new ScreenPoint(500, 220), BarPlacement.Place(caret, Width, Height, Screen));
    }

    [Fact]
    public void Too_close_to_the_right_edge_it_flips_to_the_left_of_the_anchor()
    {
        var placed = BarPlacement.Place(BarPlacement.Pointer(new ScreenPoint(1800, 200)), Width, Height, Screen);

        Assert.Equal(new ScreenPoint(1800 - Width, 200), placed);
    }

    [Fact]
    public void Low_on_the_screen_the_pointer_bar_moves_up_only_as_far_as_it_must()
    {
        // The old badge rule flipped the whole bar above the pointer (top at
        // 900 - 18 - 620 = 262); now the top stays as near the pointer as the
        // screen allows, and the left edge stays on the pointer's x.
        var placed = BarPlacement.Place(BarPlacement.Pointer(new ScreenPoint(700, 900)), Width, Height, Screen);

        Assert.Equal(new ScreenPoint(700, 1080 - Height), placed);
    }

    [Fact]
    public void Low_on_the_screen_a_caret_bar_goes_above_the_line_being_typed()
    {
        var caret = new ScreenRect(700, 880, 700, 900);

        Assert.Equal(new ScreenPoint(700, 880 - Height), BarPlacement.Place(caret, Width, Height, Screen));
    }

    [Fact]
    public void A_caret_with_no_room_above_either_falls_back_to_moving_up()
    {
        var shortScreen = new ScreenRect(0, 0, 1920, 700);
        var caret = new ScreenRect(700, 400, 700, 420);

        Assert.Equal(new ScreenPoint(700, 700 - Height), BarPlacement.Place(caret, Width, Height, shortScreen));
    }

    [Fact]
    public void On_a_screen_smaller_than_the_bar_it_is_pinned_to_the_top_left_of_the_work_area()
    {
        var tiny = new ScreenRect(100, 50, 400, 500);

        Assert.Equal(new ScreenPoint(100, 50), BarPlacement.Place(BarPlacement.Pointer(new ScreenPoint(300, 300)), Width, Height, tiny));
    }

    [Fact]
    public void A_second_monitor_with_a_negative_origin_is_honoured()
    {
        var left = new ScreenRect(-1920, 0, 0, 1080);

        Assert.Equal(new ScreenPoint(-1500, 100), BarPlacement.Place(BarPlacement.Pointer(new ScreenPoint(-1500, 100)), Width, Height, left));
    }
}
