using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 窄条卡片里的图按多少像素宽解码（用户实录 2026-10-14：窄条中的图片显示模糊）：铺满图片区要多少
/// 物理像素就解多少，但不超过图片本身。
/// </summary>
public class CardImageFitTests
{
    private const double BarWidth = 384;
    private const double ImageHeight = 120;

    [Fact]
    public void A_screenshot_fills_the_box_width_in_screen_pixels()
    {
        // 384 DIP 宽的框在 150% 的屏上是 576 个物理像素。原来存下的缩略图只有 240 像素宽，
        // 只能放大两倍多去填——发糊就是这么来的。
        var width = CardImageFit.DecodeWidth(1920, 1080, BarWidth, ImageHeight, 1.5);

        Assert.Equal(576, width);
        Assert.True(width > 240);
    }

    [Fact]
    public void A_small_picture_is_never_decoded_larger_than_itself()
        => Assert.Equal(300, CardImageFit.DecodeWidth(300, 200, BarWidth, ImageHeight, 1.5));

    [Fact]
    public void A_wide_strip_is_covered_by_its_height_and_needs_more_width()
    {
        // 3000×200 铺满 120 高的框时显示宽 1800 DIP（两边裁掉），按框宽解只会糊。
        Assert.Equal(1800, CardImageFit.DecodeWidth(3000, 200, BarWidth, ImageHeight, 1.0));
    }

    [Fact]
    public void A_tall_picture_follows_the_box_width_up_to_its_own_width()
        => Assert.Equal(500, CardImageFit.DecodeWidth(500, 2000, BarWidth, ImageHeight, 2.0));

    [Fact]
    public void Without_a_known_size_the_box_width_in_screen_pixels_is_used()
        => Assert.Equal(480, CardImageFit.DecodeWidth(0, 0, BarWidth, ImageHeight, 1.25));

    [Fact]
    public void A_scale_that_is_not_positive_counts_as_one()
        => Assert.Equal(384, CardImageFit.DecodeWidth(1920, 1080, BarWidth, ImageHeight, 0));
}
