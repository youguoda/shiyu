using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 图片预览的弹窗跟着图片走（用户需求 2026-10-28）：装得下时框就是图，装不下时框停在上限、多出的倍数进框内。
/// </summary>
public class PreviewImageZoomTests
{
    [Fact]
    public void While_it_fits_the_box_is_the_picture_itself()
    {
        // 800×500 的图在 150% 屏上一比一是 533.33×333.33 DIP，上限 1200×700 装得下。
        var (width, height, overflow) = PreviewImageZoom.Layout(533.333, 333.333, 1.0, 1200, 700, 1.5);

        Assert.Equal(800.0 / 1.5, width, precision: 3);
        Assert.Equal(500.0 / 1.5, height, precision: 3);
        Assert.Equal(1.0, overflow, precision: 3);
    }

    [Fact]
    public void Past_the_cap_the_box_stops_and_the_rest_zooms_inside()
    {
        // 4K 截图一比一（150% 屏）是 2560×1440 DIP；上限 1190×640：框按高度停在 640。
        var (width, height, overflow) = PreviewImageZoom.Layout(2560, 1440, 1.0, 1190, 640, 1.5);

        // 宽 1137.78 DIP × 1.5 = 1706.67 px，落到整像素 1707 px = 1138 DIP。
        Assert.Equal(640.0, height, precision: 3);
        Assert.Equal(1138.0, width, precision: 3);
        Assert.Equal(2560.0 / 1138, overflow, precision: 6);
    }

    [Fact]
    public void Box_sides_land_on_whole_screen_pixels()
    {
        var (width, height, _) = PreviewImageZoom.Layout(333.3, 222.2, 1.0, 2000, 2000, 1.5);

        Assert.Equal(Math.Round(width * 1.5), width * 1.5, precision: 9);
        Assert.Equal(Math.Round(height * 1.5), height * 1.5, precision: 9);
    }

    [Fact]
    public void Zoom_is_bounded_by_a_readable_minimum_and_eight_times_fit()
    {
        // 自然尺寸 2560 宽，适应档 0.25：下限让长边 64 DIP，上限 8 × 0.25 = 2。
        Assert.Equal(64.0 / 2560, PreviewImageZoom.Clamp(0.0001, 2560, 1440, fitZoom: 0.25), precision: 9);
        Assert.Equal(2.0, PreviewImageZoom.Clamp(50, 2560, 1440, fitZoom: 0.25), precision: 9);
        Assert.Equal(0.6, PreviewImageZoom.Clamp(0.6, 2560, 1440, fitZoom: 0.25), precision: 9);
    }

    [Fact]
    public void A_picture_smaller_than_the_minimum_is_never_magnified_to_reach_it()
        => Assert.Equal(1.0, PreviewImageZoom.Clamp(0.5, 40, 40, fitZoom: 1.0), precision: 9);

    [Theory]
    [InlineData(-5000.0, 600, 2.25, -750.0)]   // 拖过头：停在图片右缘贴着框右缘
    [InlineData(80.0, 600, 2.25, 0.0)]          // 往回拖过头：停在左缘
    [InlineData(-300.0, 600, 2.25, -300.0)]
    [InlineData(-10.0, 600, 1.0, 0.0)]          // 框贴着图：没得拖
    public void Panning_keeps_the_box_covered(double origin, double box, double overflow, double expected)
        => Assert.Equal(expected, PreviewImageZoom.ClampOrigin(origin, box, overflow), precision: 9);

    [Fact]
    public void A_wheel_step_is_a_quarter_in_and_a_fifth_out()
    {
        Assert.Equal(1.25, PreviewImageZoom.Step(1.0, +120), precision: 9);
        Assert.Equal(0.8, PreviewImageZoom.Step(1.0, -120), precision: 9);
    }
}
