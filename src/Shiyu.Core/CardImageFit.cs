namespace Shiyu.Core;

/// <summary>
/// 窄条卡片里的图该解码成多少像素宽（用户实录 2026-10-14：窄条中的图片显示模糊）。
///
/// 图片区按 UniformToFill 铺满「框宽 × 图片高度」：显示宽度是 max(框宽, 框高 × 宽高比)，再乘上
/// 屏幕缩放，就是屏幕上要的物理像素。解码得比这少，屏幕上就是把小图放大，这正是发糊的来源（存下
/// 的缩略图只有 240 像素宽，150% 的屏要 500 多）；解码得比图片本身多则只是白占内存，所以封顶在
/// 图片自己的宽。
///
/// Pure arithmetic: the caller measures the box and the screen scale and hands them in.
/// </summary>
public static class CardImageFit
{
    /// <param name="imageWidth">图片本身的像素宽；不知道时传 0。</param>
    /// <param name="imageHeight">图片本身的像素高；不知道时传 0。</param>
    /// <param name="boxWidth">图片区的宽（DIP）。</param>
    /// <param name="boxHeight">图片区的高（DIP）。</param>
    /// <param name="scale">屏幕像素 / DIP（150% 的屏为 1.5）；不是正数时按 1 算。</param>
    /// <returns>该解码到的像素宽，至少为 1。</returns>
    public static int DecodeWidth(int imageWidth, int imageHeight, double boxWidth, double boxHeight, double scale)
    {
        scale = scale > 0 ? scale : 1;

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            // 尺寸不知道（行早于尺寸列，或文件头读不出）：按铺满框宽算。
            return Math.Max(1, (int)Math.Ceiling(boxWidth * scale));
        }

        var shown = Math.Max(boxWidth, boxHeight * imageWidth / imageHeight);
        return Math.Clamp((int)Math.Ceiling(shown * scale), 1, imageWidth);
    }
}
