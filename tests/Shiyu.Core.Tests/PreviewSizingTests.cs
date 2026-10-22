using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class PreviewSizingTests
{
    private const double LineHeight = 24;

    [Fact]
    public void A_short_text_gets_a_panel_around_its_own_shape()
    {
        var (width, height) = PreviewSizing.ForText(lineCount: 2, textWidth: 300, lineHeight: LineHeight);

        Assert.Equal(300 + PreviewSizing.ChromeHorizontal, width);
        Assert.Equal(2 * LineHeight + PreviewSizing.ChromeVertical, height);
    }

    [Fact]
    public void A_narrow_text_is_still_wide_enough_to_read_as_a_panel()
    {
        var (width, _) = PreviewSizing.ForText(lineCount: 1, textWidth: 12, lineHeight: LineHeight);

        Assert.Equal(PreviewSizing.MinWidth, width);
    }

    [Fact]
    public void A_long_text_caps_at_both_bounds_instead_of_growing()
    {
        var (width, height) = PreviewSizing.ForText(lineCount: 400, textWidth: 5000, lineHeight: LineHeight);

        Assert.Equal(PreviewSizing.MaxWidth, width);
        Assert.Equal(PreviewSizing.MaxHeight, height);
    }

    [Fact]
    public void An_image_keeps_its_aspect_inside_the_box()
    {
        var (width, height) = PreviewSizing.ForImage(1000, 500);

        var imageWidth = width - PreviewSizing.ChromeHorizontal;
        var imageHeight = height - PreviewSizing.ChromeVertical;

        Assert.Equal(2.0, imageWidth / imageHeight, precision: 2);
        Assert.InRange(width, PreviewSizing.MinWidth, PreviewSizing.MaxWidth);
        Assert.InRange(height, PreviewSizing.MinHeight, PreviewSizing.MaxHeight);
    }

    [Fact]
    public void A_tall_portrait_image_is_limited_by_height_not_width()
    {
        var (width, height) = PreviewSizing.ForImage(500, 3000);

        Assert.Equal(PreviewSizing.MaxHeight, height);
        // Scaled to fit 3000 into the height box, then chrome added.
        Assert.True(width < PreviewSizing.MaxWidth, "a portrait image should not stretch the panel wide");
    }

    [Fact]
    public void A_small_image_is_not_blown_up_beyond_the_panel_itself()
    {
        // A 200×120 image fits the box outright; the panel wraps it, and the
        // minimum keeps the chrome from collapsing onto it.
        var (width, height) = PreviewSizing.ForImage(200, 120);

        Assert.InRange(width, PreviewSizing.MinWidth, PreviewSizing.MaxWidth);
        Assert.InRange(height, PreviewSizing.MinHeight, PreviewSizing.MaxHeight);
    }

    [Fact]
    public void A_row_without_any_size_falls_back_to_the_minimum_panel()
    {
        Assert.Equal(
            (PreviewSizing.MinWidth, PreviewSizing.MinHeight),
            PreviewSizing.ForImage(0, 0));
    }

    // --- 屏幕像素（用户实录 2026-10-09：预览图片是低分辨率的）---------------------------

    [Fact]
    public void On_a_scaled_screen_a_small_image_shows_pixel_for_pixel_not_magnified()
    {
        // 150% 屏：300×200 的图照 DIP 原样摆会占 450×300 个屏幕像素——放大 1.5 倍，糊。
        var (width, height) = PreviewSizing.FitImage(300, 200, 454, 502, deviceScale: 1.5);

        Assert.Equal(200, width, precision: 3);
        Assert.Equal(133.333, height, precision: 3);
    }

    [Fact]
    public void A_large_image_fits_the_box_whatever_the_scale()
    {
        var (width, height) = PreviewSizing.FitImage(1920, 1080, 454, 502, deviceScale: 1.5);

        Assert.Equal(454, width, precision: 3);
        Assert.Equal(383 / 1.5, height, precision: 3);
    }

    [Fact]
    public void Every_edge_lands_on_a_whole_screen_pixel()
    {
        // 800×500 塞进 454 宽：283.75 DIP 高 = 425.625 个屏幕像素。位图只能是整像素，画进这样的
        // 框还得再缩放一次——一缩放就软。框落在整像素上，解码就能正好一比一。
        var (width, height) = PreviewSizing.FitImage(800, 500, 454, 502, deviceScale: 1.5);

        Assert.Equal(681, width * 1.5, precision: 6);
        Assert.Equal(426, height * 1.5, precision: 6);
    }

    [Fact]
    public void The_original_decodes_to_exactly_the_screen_pixels_it_fills()
    {
        Assert.Equal((681, 426), PreviewSizing.DecodeSize(800, 500, (454, 284), deviceScale: 1.5));

        // 一比一显示的小图就按原图解码，一个像素也不多。
        Assert.Equal((300, 200), PreviewSizing.DecodeSize(300, 200, (200, 400 / 3.0), deviceScale: 1.5));
    }

    [Fact]
    public void The_original_is_decoded_at_screen_pixels_but_never_past_its_own()
    {
        Assert.Equal(681, PreviewSizing.DecodeWidth(1920, displayWidth: 454, deviceScale: 1.5));
        Assert.Equal(454, PreviewSizing.DecodeWidth(1920, displayWidth: 454, deviceScale: 1.0));
        Assert.Equal(300, PreviewSizing.DecodeWidth(300, displayWidth: 200, deviceScale: 1.5));

        // 旧行没有尺寸：只按显示宽度。
        Assert.Equal(681, PreviewSizing.DecodeWidth(0, displayWidth: 454, deviceScale: 1.5));
    }

    [Fact]
    public void The_panel_wraps_the_pixel_for_pixel_image_on_a_scaled_screen()
    {
        var (width, height) = PreviewSizing.ForImage(600, 400, deviceScale: 2.0);

        Assert.Equal(300 + PreviewSizing.ChromeHorizontal, width);
        Assert.Equal(200 + PreviewSizing.ChromeVertical, height);
        Assert.Equal((300.0, 200.0), PreviewSizing.ImageDisplay(600, 400, deviceScale: 2.0));
        Assert.Null(PreviewSizing.ImageDisplay(0, 400, deviceScale: 2.0));
    }

    [Fact]
    public void A_file_list_scales_with_its_rows_and_scrolls_past_the_cap()
    {
        var (smallWidth, smallHeight) = PreviewSizing.ForFiles(rowCount: 3, rowHeight: 22);
        var (largeWidth, largeHeight) = PreviewSizing.ForFiles(rowCount: 300, rowHeight: 22);

        Assert.Equal(3 * 22 + PreviewSizing.ChromeVertical, smallHeight);
        Assert.Equal(PreviewSizing.MaxWidth, smallWidth);
        Assert.Equal(PreviewSizing.MaxHeight, largeHeight);
    }

    // 4K 截图（preview-zoom-perf / preview-decode-cost 探针的期望值依据，2026-10-20）。

    [Fact]
    public void A_4k_screenshot_fits_the_box_at_whole_screen_pixels()
    {
        var (width, height) = PreviewSizing.FitImage(3840, 2160,
            PreviewSizing.MaxWidth - PreviewSizing.ChromeHorizontal,
            PreviewSizing.MaxHeight - PreviewSizing.ChromeVertical, deviceScale: 1.5);

        Assert.Equal(454.0, width);
        Assert.Equal(255.333, height, precision: 3);
    }

    [Fact]
    public void A_4k_screenshot_decodes_to_exactly_the_screen_pixels_shown()
    {
        var display = PreviewSizing.ImageDisplay(3840, 2160, deviceScale: 1.5);

        Assert.Equal((681, 383), PreviewSizing.DecodeSize(3840, 2160, display!.Value, 1.5));
    }

    // 图片面板随屏放宽（用户需求 2026-10-22：截图缩了三四倍，字看不清）。

    [Fact]
    public void Image_bounds_take_half_the_width_and_six_tenths_of_the_height()
    {
        // 2560×1400 的工作区在 150% 屏上是 1706.67×933.33 DIP。
        Assert.Equal((853.0, 560.0), PreviewSizing.ImageBounds(1706.67, 933.33));
        Assert.Equal((960.0, 630.0), PreviewSizing.ImageBounds(1920, 1050));
    }

    [Fact]
    public void Image_bounds_never_shrink_below_the_text_panel()
        => Assert.Equal((PreviewSizing.MaxWidth, PreviewSizing.MaxHeight), PreviewSizing.ImageBounds(800, 600));

    [Fact]
    public void A_screenshot_gets_the_wider_box_and_stays_whole_screen_pixels()
    {
        var bounds = PreviewSizing.ImageBounds(1920, 1050);
        var display = PreviewSizing.ImageDisplay(1920, 1080, deviceScale: 1.0, bounds)!.Value;

        // 宽受限：934 DIP 宽（960 - 26），比旧框的 454 大一倍多。
        Assert.Equal(934.0, display.Width);
        Assert.Equal(525.0, display.Height);
        Assert.Equal((960.0, 583.0), PreviewSizing.ForImage(1920, 1080, 1.0, bounds));
    }

    [Fact]
    public void A_small_image_is_still_shown_one_to_one_in_the_wider_box()
        => Assert.Equal((300.0, 200.0), PreviewSizing.ImageDisplay(600, 400, deviceScale: 2.0, PreviewSizing.ImageBounds(1920, 1050)));

    [Theory]
    [InlineData(3840, 934.0, 1.0, 4.111)]   // 缩着显示：放大到一比一要 4 倍多
    [InlineData(800, 533.333, 1.5, 1.0)]    // 本来就一比一
    [InlineData(0, 300.0, 1.0, 1.0)]        // 没有尺寸的旧行
    public void Actual_size_is_one_image_pixel_per_screen_pixel(int pixelWidth, double displayWidth, double scale, double expected)
        => Assert.Equal(expected, PreviewSizing.ActualSizeScale(pixelWidth, displayWidth, scale), precision: 3);
}
