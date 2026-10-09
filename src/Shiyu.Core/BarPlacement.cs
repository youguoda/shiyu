namespace Shiyu.Core;

/// <summary>
/// 窄条呼出时落在哪儿（用户需求 2026-10-09：窄条的左上角应该就在光标处）。
///
/// 与徽标（<see cref="BadgePlacement"/>）的规矩不同：徽标小，退开 18 px 免得压在指针下，
/// 放不下就整个翻到对边；窄条高 600 多 DIP，照徽标的规矩，指针在屏幕下半截时一呼出就
/// 翻到指针上方，左上角离光标老远，最新的那条反倒离得最远。规则：
///
/// <list type="bullet">
/// <item>左上角对准锚点：鼠标是指针尖；输入光标是它的左下角，窄条落在这一行正下方。</item>
/// <item>右边放不下：翻到锚点左侧（右上角对准锚点）——窄条只有 384 宽，翻过去只挪一小步。</item>
/// <item>下边放不下：输入光标先试这一行的上方（不挡正在打字的那行）；鼠标指针、或上方也放不下
/// 时，向上挪到刚好放得下——顶边离光标最近，最新的条目也就离得最近。</item>
/// <item>最后钳进工作区：小屏上宁可贴边，也不让窄条有一截在屏幕外。</item>
/// </list>
/// </summary>
public static class BarPlacement
{
    /// <param name="anchor">
    /// 锚点，物理像素：鼠标是零大小的矩形（<see cref="Pointer"/>）；输入光标是它那条竖线
    /// （上沿到下沿，宽为零）。
    /// </param>
    public static ScreenPoint Place(ScreenRect anchor, int width, int height, ScreenRect workArea)
    {
        var x = anchor.Left;
        if (x + width > workArea.Right)
        {
            x = anchor.Left - width;
        }

        var y = anchor.Bottom;
        if (y + height > workArea.Bottom)
        {
            var above = anchor.Top - height;
            y = anchor.Height > 0 && above >= workArea.Top
                ? above
                : workArea.Bottom - height;
        }

        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));

        return new ScreenPoint(x, y);
    }

    /// <summary>鼠标指针作锚点：指针尖，一个零大小的矩形。</summary>
    public static ScreenRect Pointer(ScreenPoint tip) => new(tip.X, tip.Y, tip.X, tip.Y);
}
