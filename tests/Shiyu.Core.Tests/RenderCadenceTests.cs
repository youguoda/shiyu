using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 流式出字时界面刷新的节拍（用户实录 2026-10-15：LLM 出字时输出一卡一卡、拖滚动条发涩、偶尔
/// "未响应"——原来每一片都当场整篇重排）。片来得再密，界面也只按节拍刷，后来的片跟着下一回一并带上。
/// </summary>
public class RenderCadenceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

    private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void The_first_piece_is_shown_at_once()
        => Assert.Equal(TimeSpan.Zero, new RenderCadence(Interval).Request(At(0)));

    [Fact]
    public void Pieces_arriving_while_a_refresh_is_queued_ride_along_with_it()
    {
        var cadence = new RenderCadence(Interval);
        cadence.Request(At(0));

        Assert.Null(cadence.Request(At(1)));
        Assert.Null(cadence.Request(At(5)));
        Assert.Null(cadence.Request(At(30)));
    }

    [Fact]
    public void After_a_refresh_the_next_waits_out_the_interval()
    {
        var cadence = new RenderCadence(Interval);
        cadence.Request(At(0));
        cadence.Rendering(At(2));

        Assert.Equal(At(30), cadence.Request(At(12)));
    }

    [Fact]
    public void A_piece_after_a_long_pause_is_shown_at_once()
    {
        var cadence = new RenderCadence(Interval);
        cadence.Request(At(0));
        cadence.Rendering(At(0));

        Assert.Equal(TimeSpan.Zero, cadence.Request(At(500)));
    }

    [Fact]
    public void A_piece_arriving_during_a_refresh_queues_exactly_one_more()
    {
        // 刷新一开始就算数：刷新进行中到的片读不到，得再排一回，而且只排一回。
        var cadence = new RenderCadence(Interval);
        cadence.Request(At(0));
        cadence.Rendering(At(0));

        Assert.NotNull(cadence.Request(At(3)));
        Assert.Null(cadence.Request(At(4)));
    }

    [Fact]
    public void The_default_refreshes_about_25_times_a_second()
        => Assert.Equal(TimeSpan.FromMilliseconds(40), RenderCadence.Default);
}
