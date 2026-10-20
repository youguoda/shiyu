using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class PreviewImageTransformTests
{
    [Fact]
    public void Four_wheel_ticks_reach_one_point_two_five_to_the_fourth()
    {
        var scale = 1.0;
        var (ox, oy) = (0.0, 0.0);
        for (var tick = 0; tick < 4; tick++)
        {
            (scale, ox, oy) = PreviewImageTransform.Wheel(scale, ox, oy, 200, 100, +120);
        }

        Assert.Equal(2.44140625, scale, precision: 9);
        Assert.Equal(200.0 * (1 - 2.44140625), ox, precision: 9);
        Assert.Equal(100.0 * (1 - 2.44140625), oy, precision: 9);
    }

    [Fact]
    public void The_image_point_under_the_cursor_stays_under_the_cursor()
    {
        // 任意状态缩放一步：元素空间光标点 p = o + s×u 下的内容坐标不变。
        var (scale, ox, oy) = (2.44140625, -288.28125, -144.140625);
        var (uX, uY) = (100.0, 80.0);
        var (px, py) = (ox + scale * uX, oy + scale * uY);

        (scale, ox, oy) = PreviewImageTransform.Wheel(scale, ox, oy, uX, uY, +120);

        Assert.Equal(uX, (px - ox) / scale, precision: 9);
        Assert.Equal(uY, (py - oy) / scale, precision: 9);
    }

    [Fact]
    public void Zooming_past_the_cap_clamps_at_eight()
    {
        var (scale, ox, oy) = PreviewImageTransform.Wheel(8.0, 10, 20, 200, 100, +120);

        Assert.Equal(8.0, scale);
        Assert.Equal(10.0, ox, precision: 9);
        Assert.Equal(20.0, oy, precision: 9);
    }

    [Fact]
    public void Zooming_back_past_one_snaps_to_identity_and_zeroes_the_origin()
    {
        var (scale, ox, oy) = PreviewImageTransform.Wheel(1.2, -30, -15, 200, 100, -120);

        Assert.Equal(1.0, scale);
        Assert.Equal(0.0, ox);
        Assert.Equal(0.0, oy);
    }
}
