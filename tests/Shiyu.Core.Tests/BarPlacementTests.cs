using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 贴边隐藏（用户需求 2026-10-20）的位置算术：收起只露条，展开完整；
/// 条比窗还高的荒唐值钳到「整个留在屏幕外」。
/// </summary>
public class BarPlacementTests
{
    private const double WorkTop = 0;
    private const double Height = 640;

    [Fact]
    public void Collapsed_leaves_only_the_strip_on_screen()
        => Assert.Equal(WorkTop - (Height - 8), BarPlacement.DockedTop(WorkTop, Height, strip: 8));

    [Fact]
    public void An_absurd_strip_clamps_to_showing_the_whole_window()
        => Assert.Equal(WorkTop, BarPlacement.DockedTop(WorkTop, Height, strip: 999));

    [Fact]
    public void Expand_restores_the_full_top()
        => Assert.Equal(WorkTop, BarPlacement.ExpandedTop(WorkTop));

    [Theory]
    [InlineData(40, true)]      // 贴着工作区顶
    [InlineData(52, true)]      // 抓取范围的边上
    [InlineData(-580, true)]    // 收起或滑到一半：顶边在工作区顶之上
    [InlineData(53, false)]
    [InlineData(300, false)]    // 拖到屏幕中间放下
    public void Only_a_bar_at_the_top_edge_rests_there(int windowTop, bool rests)
        => Assert.Equal(rests, BarPlacement.RestsAtTopEdge(windowTop, workTop: 40));
}
