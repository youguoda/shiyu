using Shiyu.Windows;

namespace Shiyu.Windows.Tests;

/// <summary>
/// 暂停记录时托盘图标的灰色版（评审 2026-10-16 F-06）：同轮廓、亮度灰、再透一点。只算像素，不碰托盘。
/// </summary>
public sealed class TrayIconImagesTests
{
    [Fact]
    public void Colours_turn_to_their_brightness_and_the_outline_fades_a_little()
    {
        // BGRA：纯红不透明、纯绿半透明、全透明。
        byte[] pixels = [0, 0, 255, 255, 0, 255, 0, 128, 10, 20, 30, 0];

        TrayIconImages.Desaturate(pixels);

        Assert.Equal(new byte[] { 76, 76, 76, 191 }, pixels[0..4]);
        Assert.Equal(new byte[] { 150, 150, 150, 96 }, pixels[4..8]);
        Assert.Equal(0, pixels[11]);
    }

    [Fact]
    public void The_apps_own_icon_has_a_gray_twin()
    {
        // 测试宿主里没有拾语自己的图标资源时，拿系统的应用图标代替——做法一样。
        var icon = TrayIcon.LoadAppIcon();
        var owned = icon != IntPtr.Zero;
        if (!owned)
        {
            icon = NativeMethods.LoadIconW(IntPtr.Zero, NativeMethods.IdiApplication);
        }

        var gray = TrayIconImages.Grayscale(icon);
        try
        {
            Assert.NotEqual(IntPtr.Zero, gray);
        }
        finally
        {
            NativeMethods.DestroyIcon(gray);
            if (owned)
            {
                NativeMethods.DestroyIcon(icon);
            }
        }
    }
}
