using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>朗读挑音色（评审 2026-10-16 F-10）：按语言挑，自然音色优先，找不到交回 SAPI。</summary>
public class SpeechVoicePickTests
{
    private static readonly VoiceCandidate[] ThisMachine =
    [
        new("Microsoft David", "en-US"),
        new("Microsoft Zira", "en-US"),
        new("Microsoft Huihui", "zh-CN"),
        new("Microsoft Yaoyao", "zh-CN"),
        new("Microsoft Kangkang", "zh-CN"),
        new("Microsoft Mark", "en-US"),
    ];

    [Fact]
    public void The_first_voice_of_the_language_is_used_when_there_is_no_natural_one()
    {
        Assert.Equal("Microsoft Huihui", SpeechVoicePick.Best(ThisMachine, "zh")?.Name);
        Assert.Equal("Microsoft David", SpeechVoicePick.Best(ThisMachine, "en")?.Name);
    }

    [Fact]
    public void A_natural_voice_wins_over_the_older_ones()
    {
        VoiceCandidate[] voices = [.. ThisMachine, new("Microsoft Xiaoxiao Online (Natural) - Chinese (Mainland)", "zh-CN")];

        Assert.StartsWith("Microsoft Xiaoxiao Online (Natural)", SpeechVoicePick.Best(voices, "zh")!.Name);
    }

    [Fact]
    public void Without_a_voice_for_the_language_it_hands_back_to_sapi()
    {
        Assert.Null(SpeechVoicePick.Best(ThisMachine, "ja"));
        Assert.Null(SpeechVoicePick.Best(ThisMachine, null));
        Assert.Null(SpeechVoicePick.Best([], "zh"));
    }
}
