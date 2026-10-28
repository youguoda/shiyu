namespace Shiyu.Core;

/// <summary>
/// 图片预览「弹窗跟着图片走」的算术（用户需求 2026-10-28）。缩放状态是 <c>zoom</c>：显示尺寸 ÷ 原图一比一的
/// 尺寸（一个图像像素占一个屏幕像素，DIP 里就是 像素 ÷ 屏幕缩放）。屏幕装得下时图片框就是图片本身，弹窗贴着
/// 图片变大变小；装不下时图片框停在上限，多出来的倍数交给框内的缩放（<c>overflow</c> ≥ 1），可拖动平移。
/// 纯算术，可单测。
/// </summary>
public static class PreviewImageZoom
{
    /// <summary>最大放大：适应框那一档的 8 倍（同 <see cref="PreviewImageTransform.MaxScale"/>）。</summary>
    public const double MaxOverFit = PreviewImageTransform.MaxScale;

    /// <summary>缩到最小时图片长边（DIP）：再小就看不出是什么了。</summary>
    public const double MinSide = 64;

    /// <summary>双击缩成的小方块：整扇窗的边长（DIP）。</summary>
    public const double SquareSide = 112;

    /// <summary>滚轮一步。</summary>
    public static double Step(double zoom, int delta) => zoom * (delta > 0 ? 1.25 : 0.8);

    /// <summary>缩放的上下限：长边不小于 <see cref="MinSide"/>（原图本来就更小时不放大它），不大于适应档的 8 倍。</summary>
    public static double Clamp(double zoom, double naturalWidth, double naturalHeight, double fitZoom)
    {
        var longest = Math.Max(naturalWidth, naturalHeight);
        var min = longest > 0 ? Math.Min(fitZoom, MinSide / longest) : fitZoom;
        return Math.Clamp(zoom, min, Math.Max(min, fitZoom * MaxOverFit));
    }

    /// <summary>
    /// 这一档缩放下的图片框（DIP）与框内还要放大的倍数。框不超过 <paramref name="maxWidth"/>×<paramref name="maxHeight"/>，
    /// 比例与图片相同，两边都落在整屏幕像素上（<paramref name="deviceScale"/>）——位图与框一比一时不再被半个像素抹糊。
    /// </summary>
    public static (double BoxWidth, double BoxHeight, double Overflow) Layout(
        double naturalWidth, double naturalHeight, double zoom, double maxWidth, double maxHeight, double deviceScale)
    {
        var device = Math.Max(1.0, deviceScale);
        var width = Math.Max(1.0 / device, naturalWidth * zoom);
        var height = Math.Max(1.0 / device, naturalHeight * zoom);
        var fit = Math.Min(1.0, Math.Min(maxWidth / width, maxHeight / height));
        var boxWidth = Math.Max(1, Math.Round(width * fit * device)) / device;
        var boxHeight = Math.Max(1, Math.Round(height * fit * device)) / device;
        return (boxWidth, boxHeight, width / boxWidth);
    }

    /// <summary>
    /// 平移量收在「图片仍盖满框」的范围里（框内放大时）：拖到底就停，不会把图拖出框露出底色。
    /// <paramref name="overflow"/> 为 1（框贴着图）时只有 0。
    /// </summary>
    public static double ClampOrigin(double origin, double box, double overflow)
        => Math.Clamp(origin, Math.Min(0, box - box * overflow), 0);
}
