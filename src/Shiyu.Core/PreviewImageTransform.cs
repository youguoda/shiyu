namespace Shiyu.Core;

/// <summary>
/// 预览工作台的图片缩放算术（用户实录 2026-10-20 的锚点漂移）：绕光标缩放，
/// 光标下的图像点缩放前后不动。光标用<b>图像内容坐标</b>（GetPosition(ImageHost)，
/// 变换反演后的读数）——渲染位置是 元素左上角 + origin + scale×内容点，取父空间
/// 光标会差一个恒定的布局偏移项（实测残漂 2.6 DIP）；用内容点则公式精确无偏。
/// 低于一比一回弹到一比一并清零 origin；放大上限 8 倍。纯算术，可单测。
/// </summary>
public static class PreviewImageTransform
{
    public const double MaxScale = 8.0;

    /// <summary>滚轮一步：delta &gt; 0 放大。返回 (新 scale, 新 originX, 新 originY)。</summary>
    public static (double Scale, double OriginX, double OriginY) Wheel(
        double scale, double originX, double originY, double cursorLocalX, double cursorLocalY, int delta)
        => Zoom(scale, originX, originY, cursorLocalX, cursorLocalY, scale * (delta > 0 ? 1.25 : 0.8));

    /// <summary>缩放到 target：绕内容坐标 (cursorLocalX, cursorLocalY)，钳制
    /// [1, <see cref="MaxScale"/>]，一比一回零。</summary>
    public static (double Scale, double OriginX, double OriginY) Zoom(
        double scale, double originX, double originY, double cursorLocalX, double cursorLocalY, double target)
    {
        var newScale = Math.Clamp(target, 1.0, MaxScale);
        if (newScale <= 1.001)
        {
            return (1.0, 0.0, 0.0);
        }

        return (
            newScale,
            originX + cursorLocalX * (scale - newScale),
            originY + cursorLocalY * (scale - newScale));
    }
}
