using Shiyu.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRtSynthesizer = Windows.Media.SpeechSynthesis.SpeechSynthesizer;
using WinRtVoice = Windows.Media.SpeechSynthesis.VoiceInformation;

namespace Shiyu.Windows;

/// <summary>
/// WinRT 朗读（评审 2026-10-16 F-10）：<c>Windows.Media.SpeechSynthesis</c> 看得见 OneCore 音色（以及装了的自然音色），
/// 老的 SAPI 只看得见几个 Desktop 音色。合成成一段音频流，交给 <see cref="MediaPlayer"/> 放；两者都是 agile 对象，
/// 不用专门的 STA 线程。同一时刻只放一段：新的一段或停止都作废手里那一段（换代号）。
/// </summary>
internal sealed class WinRtSpeech : IDisposable
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private int _generation;
    private bool _disposed;

    /// <summary>开始放 / 放完、停下、失败。可能在任何线程上触发。</summary>
    public event Action<bool>? SpeakingChanged;

    public bool IsSpeaking { get; private set; }

    /// <summary>探针用（评审 2026-10-16 F-10）：照常合成、照常报事件，只是音量为 0——核对按钮状态时不在用户机器上出声。</summary>
    public bool Muted { get; set; }

    /// <summary>按语言前缀挑一个音色（规则见 <see cref="SpeechVoicePick"/>）；这台机器上没有为 null。</summary>
    public static WinRtVoice? VoiceFor(string? culturePrefix)
    {
        try
        {
            var voices = WinRtSynthesizer.AllVoices.ToList();
            var pick = SpeechVoicePick.Best(voices.Select(voice => new VoiceCandidate(voice.DisplayName, voice.Language)), culturePrefix);
            return pick is null ? null : voices.First(voice => voice.DisplayName == pick.Name && voice.Language == pick.Language);
        }
        catch (Exception)
        {
            // expected: 精简版系统没有 OneCore 语音——当作没有，回落 SAPI。
            return null;
        }
    }

    /// <summary>合成并开始放；合成途中被停止或被新的一段顶掉，就什么也不放。失败抛给调用方（它回落 SAPI）。</summary>
    public async Task SpeakAsync(string text, WinRtVoice voice)
    {
        var generation = Interlocked.Increment(ref _generation);
        using var synthesizer = new WinRtSynthesizer { Voice = voice };
        var stream = await synthesizer.SynthesizeTextToStreamAsync(text);

        var player = new MediaPlayer { AutoPlay = false };
        player.Volume = Muted ? 0 : 1;

        // 一句朗读不是一段媒体：不进系统的媒体控件（音量浮层、锁屏上的"正在播放"）。
        player.CommandManager.IsEnabled = false;
        player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        player.MediaEnded += (_, _) => Finish(player);
        player.MediaFailed += (_, _) => Finish(player);

        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                player.Dispose();
                stream.Dispose();
                return;
            }

            _player?.Dispose();
            _player = player;
            IsSpeaking = true;
        }

        player.Play();
        SpeakingChanged?.Invoke(true);
    }

    /// <summary>停下手里这一段，作废还在合成的那一段。</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        bool was;
        lock (_gate)
        {
            was = IsSpeaking;
            _player?.Dispose();
            _player = null;
            IsSpeaking = false;
        }

        if (was)
        {
            SpeakingChanged?.Invoke(false);
        }
    }

    private void Finish(MediaPlayer player)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_player, player))
            {
                return;
            }

            _player = null;
            IsSpeaking = false;
        }

        // 不在它自己的事件里拆它：放到线程池上再放掉。
        _ = Task.Run(player.Dispose);
        SpeakingChanged?.Invoke(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Stop();
    }
}
