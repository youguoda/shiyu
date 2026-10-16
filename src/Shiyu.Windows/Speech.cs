using System.Speech.Synthesis;
using System.Windows.Threading;

namespace Shiyu.Windows;

/// <summary>
/// 朗读服务。先走 WinRT（评审 2026-10-16 F-10：看得见 OneCore 与自然音色，见 <see cref="WinRtSpeech"/>），
/// 这种语言 WinRT 没有音色、或 WinRT 出错时，回落 SAPI（System.Speech）。
///
/// SAPI 的 SpeechSynthesizer 是 COM 对象：诞生、选音色、说话、取消、释放都必须
/// 发生在同一条 STA 线程上。这里用一条专用线程持有它整个生命周期，UI
/// 线程只投递动作、绝不触碰对象本身——与 WinVHook 同一条纪律：回调只
/// 上报，工作在 owning 线程。合成器在第一次朗读时才创建：不朗读的用户
/// 不为一个静默的 COM 对象付线程开销。
/// </summary>
public sealed class SpeechSynthesis : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEvent _ready = new(false);
    private readonly Dictionary<string, string?> _voiceByPrefix = [];
    private readonly Dictionary<string, global::Windows.Media.SpeechSynthesis.VoiceInformation?> _winRtVoiceByPrefix = [];
    private readonly WinRtSpeech _winRt = new();

    private Dispatcher? _dispatcher;
    private SpeechSynthesizer? _synthesizer;
    private volatile bool _sapiSpeaking;
    private bool _disposed;

    /// <summary>
    /// 开始读 / 读完、停下（评审 2026-10-16 F-10：朗读中按钮显示「停止」）。可能在任何线程上触发，订阅方自己回
    /// 界面线程。
    /// </summary>
    public event Action<bool>? SpeakingChanged;

    /// <summary>此刻有没有在读（两条路任一）。</summary>
    public bool IsSpeaking => _winRt.IsSpeaking || _sapiSpeaking;

    /// <summary>
    /// 探针用（评审 2026-10-16 F-10）：两条路都照常走、照常报事件，只是音量为 0——核对"朗读中显示停止"时不在
    /// 用户机器上出声。
    /// </summary>
    public bool Muted
    {
        get => _winRt.Muted;
        set => _winRt.Muted = value;
    }

    /// <summary>上一次朗读用的是哪条路、哪个音色（「winrt:Microsoft Huihui」/「sapi:…」），给探针看。</summary>
    public string? LastVoice { get; private set; }

    public SpeechSynthesis()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Shiyu 语音",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Run 在拿到 Dispatcher 之后才放行；WaitOne 的内存屏障让构造
        // 返回后 _dispatcher 一定可见。
        _ready.WaitOne();

        _winRt.SpeakingChanged += speaking => SpeakingChanged?.Invoke(speaking);
    }

    /// <summary>
    /// 朗读；已在朗读则这次点击就是停止。
    /// </summary>
    /// <param name="text">要读的译文。</param>
    /// <param name="culturePrefix">
    /// 译文语言的 TwoLetterISOLanguageName（"zh"/"en"/…），据此挑音色；
    /// null 用默认音色。见 <see cref="Core.SpeechLanguage"/>。
    /// </param>
    public void SpeakOrStop(string text, string? culturePrefix)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (IsSpeaking)
        {
            Stop();
            return;
        }

        // WinRT 有这种语言的音色就走它（评审 2026-10-16 F-10）；它出错了再回落 SAPI。
        if (WinRtVoiceFor(culturePrefix) is { } voice)
        {
            LastVoice = "winrt:" + voice.DisplayName;
            _ = SpeakWinRtAsync(text, voice, culturePrefix);
            return;
        }

        SpeakSapi(text, culturePrefix);
    }

    private global::Windows.Media.SpeechSynthesis.VoiceInformation? WinRtVoiceFor(string? culturePrefix)
    {
        if (culturePrefix is null)
        {
            return null;
        }

        lock (_winRtVoiceByPrefix)
        {
            if (!_winRtVoiceByPrefix.TryGetValue(culturePrefix, out var voice))
            {
                voice = WinRtSpeech.VoiceFor(culturePrefix);
                _winRtVoiceByPrefix[culturePrefix] = voice;
            }

            return voice;
        }
    }

    private async Task SpeakWinRtAsync(
        string text, global::Windows.Media.SpeechSynthesis.VoiceInformation voice, string? culturePrefix)
    {
        try
        {
            await _winRt.SpeakAsync(text, voice);
        }
        catch (Exception)
        {
            // expected: 合成或播放失败（音频设备、语音包被拔）——回落 SAPI，朗读仍是锦上添花。
            SpeakSapi(text, culturePrefix);
        }
    }

    private void SpeakSapi(string text, string? culturePrefix)
    {
        Post(() =>
        {
            var synthesizer = EnsureSynthesizer();
            if (synthesizer is null)
            {
                return;
            }

            try
            {
                if (synthesizer.State == SynthesizerState.Speaking)
                {
                    synthesizer.SpeakAsyncCancelAll();
                    return;
                }

                SelectVoice(synthesizer, culturePrefix);
                synthesizer.Volume = Muted ? 0 : 100;
                LastVoice = "sapi:" + synthesizer.Voice?.Name;
                synthesizer.SpeakAsync(text);
            }
            catch (Exception)
            {
                // expected: 朗读是锦上添花——没有声音设备、没有音色，
                // 都静默无效。
            }
        });
    }

    /// <summary>停止当前朗读（面板关闭时叫停，不留一个读完了没人听的语音）。</summary>
    public void Stop()
    {
        _winRt.Stop();
        Post(() =>
        {
            try
            {
                _synthesizer?.SpeakAsyncCancelAll();
            }
            catch (Exception)
            {
                // expected: 停不下来的朗读随线程关停一起消失。
            }
        });
    }

    private void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ready.Set();
        Dispatcher.Run();
    }

    private SpeechSynthesizer? EnsureSynthesizer()
    {
        if (_synthesizer is not null)
        {
            return _synthesizer;
        }

        try
        {
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();

            // 读着 / 读完，同 WinRT 那条路一样报出去（评审 2026-10-16 F-10）。
            _synthesizer.SpeakStarted += (_, _) =>
            {
                _sapiSpeaking = true;
                SpeakingChanged?.Invoke(true);
            };
            _synthesizer.SpeakCompleted += (_, _) =>
            {
                _sapiSpeaking = false;
                SpeakingChanged?.Invoke(false);
            };
        }
        catch (Exception)
        {
            // expected: 没有 SAPI 或没有音频输出——朗读按钮从此静默无效。
            _synthesizer = null;
        }

        return _synthesizer;
    }

    /// <summary>按语言前缀选音色，查过的前缀记住结论——枚举音色不便宜。</summary>
    private void SelectVoice(SpeechSynthesizer synthesizer, string? culturePrefix)
    {
        if (culturePrefix is null)
        {
            return;
        }

        if (_voiceByPrefix.TryGetValue(culturePrefix, out var cached))
        {
            if (cached is not null)
            {
                TrySelect(synthesizer, cached);
            }

            return;
        }

        string? match = null;
        try
        {
            match = synthesizer.GetInstalledVoices()
                .FirstOrDefault(voice => voice.Enabled && VoiceMatches(voice, culturePrefix))
                ?.VoiceInfo.Name;
        }
        catch (Exception)
        {
            // expected: 装了 SAPI 但音色枚举失败——按"没有匹配音色"记下，用默认。
        }

        _voiceByPrefix[culturePrefix] = match;
        if (match is not null)
        {
            TrySelect(synthesizer, match);
        }
    }

    private static bool VoiceMatches(InstalledVoice voice, string prefix)
    {
        try
        {
            return voice.VoiceInfo.Culture.TwoLetterISOLanguageName
                .StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // expected: 音色信息读不出——当作不匹配。
            return false;
        }
    }

    private static void TrySelect(SpeechSynthesizer synthesizer, string name)
    {
        try
        {
            synthesizer.SelectVoice(name);
        }
        catch (Exception)
        {
            // expected: 音色在这台机器上消失（拔掉的语音包）——留在默认音色上。
        }
    }

    /// <summary>把动作排到语音线程。关机后的迟到调用直接丢弃。</summary>
    private void Post(Action work)
    {
        var dispatcher = _dispatcher;
        if (_disposed || dispatcher is null)
        {
            return;
        }

        try
        {
            dispatcher.Invoke(work);
        }
        catch (Exception)
        {
            // expected: 线程已停（应用关机竞态）——朗读没有非完成不可的理由。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _winRt.Dispose();

        try
        {
            _dispatcher?.Invoke(() =>
            {
                try
                {
                    _synthesizer?.SpeakAsyncCancelAll();
                    _synthesizer?.Dispose();
                }
                catch (Exception)
                {
                    // expected: 关机路径尽力而为——释放失败不再有后果。
                }

                _synthesizer = null;
            });

            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _ready.Dispose();
        }
    }
}
