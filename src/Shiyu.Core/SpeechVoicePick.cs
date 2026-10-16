namespace Shiyu.Core;

/// <param name="Name">音色的显示名（Microsoft Huihui、Microsoft Xiaoxiao Online (Natural)……）。</param>
/// <param name="Language">BCP-47 语言标记（zh-CN、en-US……）。</param>
public sealed record VoiceCandidate(string Name, string Language);

/// <summary>
/// 朗读挑音色（评审 2026-10-16 F-10）：按译文语言挑，名字里带 Natural 的自然音色优先，没有就用这种语言的第一个
/// （系统列出的次序里，默认音色在前）。找不到这种语言的音色为 null——调用方回落到老的 SAPI 朗读。
/// </summary>
public static class SpeechVoicePick
{
    public static VoiceCandidate? Best(IEnumerable<VoiceCandidate> voices, string? culturePrefix)
    {
        if (string.IsNullOrWhiteSpace(culturePrefix))
        {
            return null;
        }

        var matching = voices
            .Where(voice => voice.Language.StartsWith(culturePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matching.FirstOrDefault(voice => voice.Name.Contains("Natural", StringComparison.OrdinalIgnoreCase))
            ?? matching.FirstOrDefault();
    }
}
