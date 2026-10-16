using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 拾语的模拟按键能不能到达目标窗口（v3 票 30 / 评审 2026-10-16 F-03）：目标的完整性级别高于自己，按键
/// 会被 Windows 静默吞掉；读不到按"高于"算。
/// </summary>
public class InputReachTests
{
    private const uint Low = 0x1000;
    private const uint Medium = 0x2000;
    private const uint High = 0x3000;
    private const uint System = 0x4000;

    [Theory]
    [InlineData(Medium, Medium, false)]
    [InlineData(Low, Medium, false)]
    [InlineData(High, Medium, true)]
    [InlineData(System, Medium, true)]
    [InlineData(High, High, false)]
    public void Input_is_blocked_only_on_the_way_up(uint target, uint self, bool blocked)
        => Assert.Equal(blocked, InputReach.Blocked(target, self));

    [Fact]
    public void A_target_whose_level_cannot_be_read_counts_as_higher()
    {
        // 读不到通常恰恰因为对方是提权进程：宁可多提示一句，也不让译文无声地消失。
        Assert.True(InputReach.Blocked(null, Medium));
    }
}
