namespace Shiyu.Core;

/// <summary>
/// 反向输入框的摆放（票 43）：默认贴在插入符下方；下方空间不够时翻到上方，翻到上方后保持底边贴着
/// 锚点、向上长（y + 旧高 − 新高）。翻转与钳制复用 <see cref="BadgePlacement"/>，只补"长高"这一块。
///
/// 记的是<b>理想的边</b>（上方时是底边、下方时是顶边）而不是上一次被钳制后的结果：
/// 窗口被屏幕边缘挡过一次，缩回去仍然回到原来的位置。
/// </summary>
/// <param name="X">水平位置，一次定下，长高不改它。</param>
/// <param name="Edge">理想的边：<paramref name="Above"/> 时是底边的 y，否则是顶边的 y。</param>
public sealed record ReversePlacement(int X, int Edge, bool Above)
{
    /// <summary>窗口此刻这么高时该在的位置：上方时底边不动，再钳进工作区。</summary>
    public ScreenPoint PositionFor(int height, ScreenRect workArea)
    {
        var top = Above ? Edge - height : Edge;
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new ScreenPoint(X, top);
    }
}

public static class ReverseInputPlacement
{
    /// <summary>整窗高度的下限（DIP）。</summary>
    public const int MinHeightDip = 80;

    /// <summary>整窗高度的上限（DIP）：输入框 52→180、输出再往下长，再高就该滚动了。</summary>
    public const int MaxHeightDip = 800;

    /// <summary>
    /// 第一次摆放。<paramref name="anchor"/> 是插入符的左下角（取不到插入符时是鼠标）；
    /// <paramref name="gap"/> 是与锚点的间隙（物理像素）——上方时它同时让底边避开插入符那一行。
    /// </summary>
    public static ReversePlacement Place(
        ScreenPoint anchor, int width, int height, ScreenRect workArea, int gap)
    {
        // 位置（含水平翻转与钳制）交给 BadgePlacement；这里只额外回答"翻没翻到上方"，
        // 判据与它的垂直翻转是同一个：下方放不下。
        var placed = BadgePlacement.Place(anchor, width, height, workArea, gap);
        var above = anchor.Y + gap + height > workArea.Bottom;

        return new ReversePlacement(placed.X, above ? anchor.Y - gap : anchor.Y + gap, above);
    }

    /// <summary>
    /// 用户把窗口拖到了 <paramref name="topLeft"/>（用户需求 2026-10-09，只管这一次）：从这里起顶边
    /// 停在拖到的地方、向下长，碰到屏幕底再整体上推——拖到哪儿就在哪儿，长高不会把它拽回插入符旁。
    /// 下次呼出由 <see cref="Place"/> 重新摆。
    /// </summary>
    public static ReversePlacement Dragged(ScreenPoint topLeft) => new(topLeft.X, topLeft.Y, Above: false);

    /// <summary>
    /// 整窗高度钳在 80–800 DIP；工作区矮的屏幕上，上限随工作区走（留 16 的余量），但不会低过下限。
    /// </summary>
    public static double ClampHeightDip(double height, double workAreaHeightDip)
    {
        var cap = Math.Max(MinHeightDip, Math.Min(MaxHeightDip, workAreaHeightDip - 16));
        return Math.Clamp(height, MinHeightDip, cap);
    }
}
