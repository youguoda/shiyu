namespace Shiyu.Core;

/// <summary>
/// The preview panel's final size, worked out from numbers the caller already
/// has — text wrapped-line count, image pixel size, file row count — so the
/// window opens at the size it will stay (ticket 17: a panel that grows after
/// appearing reads as a guess, not an answer).
///
/// Pure arithmetic: the caller measures the text with the real font (that is a
/// font question, not a layout one) and hands the line count in.
/// </summary>
public static class PreviewSizing
{
    /// <summary>Panel bounds in device-independent units, tuned beside a 360-wide bar.</summary>
    public const double MaxWidth = 480;

    public const double MaxHeight = 560;
    public const double MinWidth = 240;
    public const double MinHeight = 96;

    /// <summary>
    /// What one panel adds around its content: the header row, the paddings,
    /// the border. One pair for every kind, so the kinds stay visually
    /// consistent when the bar flicks between them.
    /// </summary>
    public const double ChromeVertical = 58;

    public const double ChromeHorizontal = 26;

    /// <summary>The panel size for a text entry.</summary>
    /// <param name="lineCount">Wrapped lines at <paramref name="textWidth"/>, measured by the caller.</param>
    /// <param name="textWidth">The width the text was measured against; the widest un-wrapped run when it fits.</param>
    /// <param name="lineHeight">One body line, from the design tokens.</param>
    public static (double Width, double Height) ForText(
        int lineCount, double textWidth, double lineHeight)
    {
        lineCount = Math.Max(1, lineCount);

        // Width follows the text until the panel would outgrow its bounds;
        // height follows the lines, capped where the body starts to scroll.
        var width = Math.Clamp(textWidth + ChromeHorizontal, MinWidth, MaxWidth);
        var height = Math.Clamp(lineCount * lineHeight + ChromeVertical, MinHeight, MaxHeight);

        return (Math.Round(width), Math.Round(height));
    }

    /// <summary>
    /// The panel size for an image entry: the picture scaled to fit the box
    /// whole, panel and all — never a cropped image and never a resize after
    /// the fact, because the pixels' shape is known before anything loads.
    /// </summary>
    /// <param name="deviceScale">屏幕像素 / DIP（150% 屏为 1.5）：小图按屏幕像素一比一摆，见 <see cref="FitImage"/>。</param>
    public static (double Width, double Height) ForImage(int pixelWidth, int pixelHeight, double deviceScale = 1.0)
    {
        if (ImageDisplay(pixelWidth, pixelHeight, deviceScale) is not { } image)
        {
            // A row that predates the size columns and lost its thumbnail:
            // the smallest honest panel, filled by whatever can be decoded.
            return (MinWidth, MinHeight);
        }

        var width = image.Width + ChromeHorizontal;
        var height = image.Height + ChromeVertical;

        return (Math.Round(Math.Clamp(width, MinWidth, MaxWidth)), Math.Round(Math.Clamp(height, MinHeight, MaxHeight)));
    }

    /// <summary>
    /// 预览面板里图片自己的显示尺寸（DIP）：塞进面板的图片区；没有尺寸的旧行为 null，
    /// 由解码出来的图自己定。缩略图与原图都画进这同一个框——换图时不挪、不跳。
    /// </summary>
    public static (double Width, double Height)? ImageDisplay(int pixelWidth, int pixelHeight, double deviceScale)
        => pixelWidth > 0 && pixelHeight > 0
            ? FitImage(pixelWidth, pixelHeight, MaxWidth - ChromeHorizontal, MaxHeight - ChromeVertical, deviceScale)
            : null;

    /// <summary>
    /// 一张图在一个框里显示多大（DIP）：等比塞进框，而且一个图像像素至多占一个屏幕像素。
    /// "Never magnify" once read as one image pixel per DIP — on a 150% screen
    /// that is still a 1.5× blow-up, and the preview looked low-resolution
    /// (用户实录 2026-10-09). A screenshot's pixels are the screen's pixels;
    /// one to one is exactly what the user captured, and a 40×40 favicon
    /// previews as itself.
    ///
    /// Both sides land on whole screen pixels, so the original — decoded to
    /// exactly that many pixels (<see cref="DecodeSize"/>) — maps one bitmap
    /// pixel to one screen pixel: no second resampling left to soften it.
    /// </summary>
    public static (double Width, double Height) FitImage(
        int pixelWidth, int pixelHeight, double boxWidth, double boxHeight, double deviceScale)
    {
        var device = Math.Max(1.0, deviceScale);
        var scale = Math.Min(Math.Min(boxWidth / pixelWidth, boxHeight / pixelHeight), 1.0 / device);

        return (
            Math.Max(1, Math.Round(pixelWidth * scale * device)) / device,
            Math.Max(1, Math.Round(pixelHeight * scale * device)) / device);
    }

    /// <summary>
    /// 原图解码成多大（像素）：正好是它显示时占的屏幕像素——宽高都定下，位图与框一比一、
    /// 严丝合缝（只定宽时高由比例算，差出的一个像素又让边停在像素中间）。不超过原图本身。
    /// </summary>
    public static (int Width, int Height) DecodeSize(
        int pixelWidth, int pixelHeight, (double Width, double Height) display, double deviceScale)
    {
        var device = Math.Max(1.0, deviceScale);
        return (
            Math.Clamp((int)Math.Round(display.Width * device), 1, Math.Max(1, pixelWidth)),
            Math.Clamp((int)Math.Round(display.Height * device), 1, Math.Max(1, pixelHeight)));
    }

    /// <summary>
    /// 原图解码到多宽（像素），给不知道原图尺寸的旧行用（知道时用 <see cref="DecodeSize"/>）：
    /// 显示宽度换成屏幕像素——少了就糊（原来按 DIP 解码，150% 屏上再被放大 1.5 倍），多了白占内存；
    /// 也不超过原图本身。减去的千分之一吸收浮点误差：299.99999999999994 不该因此进成 301。
    /// </summary>
    public static int DecodeWidth(int pixelWidth, double displayWidth, double deviceScale)
    {
        var screen = (int)Math.Ceiling(displayWidth * Math.Max(1.0, deviceScale) - 0.001);
        return Math.Max(1, pixelWidth > 0 ? Math.Min(pixelWidth, screen) : screen);
    }

    /// <summary>The panel size for a file entry: every row shown, scrolling only past the cap.</summary>
    public static (double Width, double Height) ForFiles(int rowCount, double rowHeight)
    {
        rowCount = Math.Max(1, rowCount);

        // Paths are long and the panel sits beside a card that shows the
        // short names; full width is the useful shape here.
        var height = Math.Clamp(rowCount * rowHeight + ChromeVertical, MinHeight, MaxHeight);

        return (MaxWidth, Math.Round(height));
    }
}
