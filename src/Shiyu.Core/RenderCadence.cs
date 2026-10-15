namespace Shiyu.Core;

/// <summary>
/// 流式出字时界面刷新的节拍（用户实录 2026-10-15：LLM 出字时输出一卡一卡、拖滚动条发涩、偶尔
/// "未响应"）。模型每秒可以送来上百片，原来每一片都当场重排一遍整篇；现在片来得再密，界面也只按
/// 节拍刷：一回刷新排着的时候，后来的片不再另排，等这一回刷新时一并带上；两回刷新之间至少隔
/// <paramref name="interval"/>。
///
/// 只管"要不要排、隔多久排"，不碰界面：片段的到达在后台线程，刷新在 UI 线程，两边都会来问，
/// 所以加锁。时间由调用方给，测试不必真等。
/// </summary>
public sealed class RenderCadence(TimeSpan interval)
{
    /// <summary>界面刷新的默认节拍：每秒 25 回，出字看着是连贯的。</summary>
    public static TimeSpan Default { get; } = TimeSpan.FromMilliseconds(40);

    private readonly object _gate = new();
    private bool _pending;
    private TimeSpan? _lastRender;

    /// <summary>
    /// 来了一片（任何线程）。该排一回刷新就给出多久之后刷（0 = 立刻）；已经排着一回，给 null——
    /// 那一回刷新时会读到这一片。
    /// </summary>
    public TimeSpan? Request(TimeSpan now)
    {
        lock (_gate)
        {
            if (_pending)
            {
                return null;
            }

            _pending = true;
            if (_lastRender is not { } last)
            {
                return TimeSpan.Zero;
            }

            var wait = interval - (now - last);
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    /// <summary>刷新开始（UI 线程）：此后到的片排下一回。</summary>
    public void Rendering(TimeSpan now)
    {
        lock (_gate)
        {
            _pending = false;
            _lastRender = now;
        }
    }
}
