using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>内容字号（ADR-0012 排版 2，用户需求 2026-10-09）。</summary>
public class ContentRampTests
{
    private static readonly ContentFontSize[] Levels =
    [
        ContentFontSize.Smaller, ContentFontSize.Small, ContentFontSize.Standard,
        ContentFontSize.Large, ContentFontSize.Larger,
    ];

    [Fact]
    public void Standard_is_exactly_the_fixed_tokens()
    {
        // 默认档不许偷偷改变任何人的界面：与原来的固定令牌逐个相等。
        var standard = ContentRamp.For(ContentFontSize.Standard);

        Assert.Equal(DesignTokens.TypeContent, standard.Content);
        Assert.Equal(DesignTokens.LineForContent, standard.ContentLine);
        Assert.Equal(DesignTokens.TypeContentMono, standard.Mono);
        Assert.Equal(DesignTokens.LineForContentMono, standard.MonoLine);
        Assert.Equal(DesignTokens.TypeHeadword, standard.Headword);
    }

    [Fact]
    public void The_levels_are_14_16_18_20_22()
    {
        // 用户需求 2026-10-10：「小」下面再加「较小」一档（14），每档仍差 2。
        Assert.Equal([14.0, 16, 18, 20, 22], Levels.Select(level => ContentRamp.For(level).Content));
    }

    [Fact]
    public void The_enum_order_is_the_order_on_screen()
    {
        // 分段按钮存的是下标、设置里存的是名字：两边靠声明顺序对上——「较小」排在最前。
        Assert.Equal(Levels, Enum.GetValues<ContentFontSize>());
    }

    [Fact]
    public void Paths_never_shrink_below_the_caption_size()
    {
        // 等宽字给路径与文件行用：再小于说明文字（12）就读不清了，「较小」档也守住这条线。
        Assert.All(Levels.Select(ContentRamp.For), ramp => Assert.True(ramp.Mono >= DesignTokens.TypeCaption));
    }

    [Fact]
    public void Every_level_keeps_reading_line_heights_and_stays_ordered()
    {
        foreach (var ramp in Levels.Select(ContentRamp.For))
        {
            // 中文正文的舒适行距在 1.6 倍以上；紧凑档同理。
            Assert.True(ramp.ContentLine >= ramp.Content * 1.6, $"content {ramp.Content}/{ramp.ContentLine}");
            Assert.True(ramp.MonoLine >= ramp.Mono * 1.6, $"mono {ramp.Mono}/{ramp.MonoLine}");
            Assert.True(ramp.CompactLine >= ramp.Compact * 1.6, $"compact {ramp.Compact}/{ramp.CompactLine}");

            // 词头比正文大，紧凑档比正文小一档，等宽字比正文小。
            Assert.True(ramp.Headword > ramp.Content);
            Assert.Equal(ramp.Content - 2, ramp.Compact);
            Assert.True(ramp.Mono < ramp.Content);
        }
    }

    [Fact]
    public void Controls_are_not_in_the_ramp()
    {
        // ADR-0012 排版 3：内容字号不碰控件文字——令牌表里控件字号仍是固定的 14。
        Assert.Equal(14, DesignTokens.TypeBody);
    }
}
