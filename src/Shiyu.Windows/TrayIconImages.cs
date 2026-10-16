using System.Runtime.InteropServices;

namespace Shiyu.Windows;

/// <summary>
/// 托盘图标的灰色版（评审 2026-10-16 F-06：暂停记录时托盘换灰态）。照着正在用的那个图标现做：
/// 同尺寸、同轮廓，颜色换成亮度灰、再透一点——一眼看得出"这会儿没在记"，又认得出是拾语。
/// 不另带一份图片资源，图标换了版也不会两头对不上。
/// </summary>
internal static class TrayIconImages
{
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    /// <summary>给一个图标做灰色版；做不出来（不是 32 位色、GDI 拒绝）给 IntPtr.Zero，调用方照用原图标。</summary>
    public static IntPtr Grayscale(IntPtr icon)
    {
        if (icon == IntPtr.Zero || !NativeMethods.GetIconInfo(icon, out var info))
        {
            return IntPtr.Zero;
        }

        try
        {
            if (info.hbmColor == IntPtr.Zero
                || NativeMethods.GetObject(info.hbmColor, Marshal.SizeOf<NativeMethods.BitmapHeader>(), out var bitmap) == 0
                || bitmap.bmWidth <= 0 || bitmap.bmHeight <= 0)
            {
                return IntPtr.Zero;
            }

            var header = new NativeMethods.BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                biWidth = bitmap.bmWidth,
                biHeight = -bitmap.bmHeight, // 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            };

            var pixels = new byte[bitmap.bmWidth * bitmap.bmHeight * 4];
            var screen = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                if (NativeMethods.GetDIBits(
                        screen, info.hbmColor, 0, (uint)bitmap.bmHeight, pixels, ref header, DibRgbColors) == 0)
                {
                    return IntPtr.Zero;
                }

                Desaturate(pixels);

                var gray = NativeMethods.CreateDIBSection(screen, ref header, DibRgbColors, out var bits, IntPtr.Zero, 0);
                if (gray == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                try
                {
                    Marshal.Copy(pixels, 0, bits, pixels.Length);
                    var grayInfo = info with { hbmColor = gray };
                    return NativeMethods.CreateIconIndirect(ref grayInfo);
                }
                finally
                {
                    NativeMethods.DeleteObject(gray);
                }
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }
        finally
        {
            NativeMethods.DeleteObject(info.hbmColor);
            NativeMethods.DeleteObject(info.hbmMask);
        }
    }

    /// <summary>
    /// BGRA 逐像素换成亮度灰，透明度降到四分之三——轮廓照旧，边缘仍是平滑的。颜色不压暗：深色任务栏上
    /// 暗灰就看不见了。
    /// </summary>
    internal static void Desaturate(byte[] bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var luminance = (byte)Math.Round((0.114 * bgra[i]) + (0.587 * bgra[i + 1]) + (0.299 * bgra[i + 2]));
            bgra[i] = luminance;
            bgra[i + 1] = luminance;
            bgra[i + 2] = luminance;
            bgra[i + 3] = (byte)Math.Round(bgra[i + 3] * 0.75);
        }
    }
}
