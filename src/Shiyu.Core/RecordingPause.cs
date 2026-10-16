namespace Shiyu.Core;

/// <summary>
/// 暂停记录（评审 2026-10-16 F-06）：共享屏幕、演示、处理不在排除名单里的敏感内容时，用户不必退出拾语。
/// 暂停期间剪贴板监听照收通知，但不落库、不弹徽标；到时自动恢复——判定按复制那一刻的时间，不等定时器。
///
/// 不跨重启：重启即恢复记录。一个忘了的"暂停"不该悄悄让历史从此空着。
/// </summary>
public sealed class RecordingPause
{
    private readonly object _gate = new();
    private DateTimeOffset? _until;
    private bool _indefinite;

    /// <summary>暂停、恢复、改期时（任何线程）。</summary>
    public event Action? Changed;

    public bool IsPaused(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _indefinite || (_until is { } until && now < until);
        }
    }

    /// <summary>定时暂停的恢复时刻；没暂停或"直到恢复"时为 null。</summary>
    public DateTimeOffset? Until
    {
        get
        {
            lock (_gate)
            {
                return _indefinite ? null : _until;
            }
        }
    }

    public void PauseFor(TimeSpan duration, DateTimeOffset now)
    {
        lock (_gate)
        {
            _indefinite = false;
            _until = now + duration;
        }

        Changed?.Invoke();
    }

    public void PauseUntilResumed()
    {
        lock (_gate)
        {
            _indefinite = true;
            _until = null;
        }

        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_gate)
        {
            _indefinite = false;
            _until = null;
        }

        Changed?.Invoke();
    }

    /// <summary>托盘提示与说明里的那一句；没暂停给 null。时刻按 <paramref name="now"/> 的时区写。</summary>
    public string? Describe(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_indefinite)
            {
                return "已暂停记录剪贴板，直到你恢复";
            }

            return _until is { } until && now < until
                ? $"已暂停记录剪贴板，{until.ToOffset(now.Offset):HH:mm} 自动恢复"
                : null;
        }
    }
}
