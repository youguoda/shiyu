using System.Globalization;

namespace Shiyu.Core;

/// <summary>免费引擎里的哪一家（票 41：必应为主，腾讯兜底）。</summary>
public enum FreeEngineName
{
    Bing,
    Tencent,
}

/// <summary>
/// 免费引擎的近况（评审 2026-10-16 F-14）：免费引擎坏了只能发版修，用户却看不到它此刻怎样。设置的
/// 翻译方式卡据此写出上次成功、上次失败各是几点、哪一家，以及此刻在用哪一家。
///
/// 只记时间与引擎名，不记原文、译文、语言；进程级，重启从空白开始——它回答的是"刚才那几次怎么了"，
/// 不是一份要存盘的履历。线程安全：翻译在后台线程上记，设置窗在界面线程上读。
/// </summary>
public sealed class FreeEngineStatus
{
    private readonly FreeEngineHealth _health;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private (DateTimeOffset At, FreeEngineName Engine)? _lastSuccess;
    private (DateTimeOffset At, bool BothFailed)? _lastFailure;

    internal FreeEngineStatus(FreeEngineHealth health, TimeProvider clock)
    {
        _health = health;
        _clock = clock;
    }

    /// <summary>生产用：与 <see cref="FreeEngineHealth.Shared"/> 同为进程级。</summary>
    public static FreeEngineStatus Shared { get; } = new(FreeEngineHealth.Shared, TimeProvider.System);

    /// <summary>记了新的一笔。可能在任何线程上触发；订阅方自己回界面线程。</summary>
    public event Action? Changed;

    /// <summary>一次翻译整段译完；<paramref name="engine"/> 是译最后一块的那一家。</summary>
    internal void RecordSuccess(FreeEngineName engine)
    {
        lock (_gate)
        {
            _lastSuccess = (_clock.GetUtcNow(), engine);
        }

        Changed?.Invoke();
    }

    /// <summary>必应没给出译文，这次从这一块起改走腾讯。</summary>
    internal void RecordBingFailure() => RecordFailure(bothFailed: false);

    /// <summary>两家都没给出译文：用户看到的是「免费引擎暂时不可用」。</summary>
    internal void RecordUnavailable() => RecordFailure(bothFailed: true);

    private void RecordFailure(bool bothFailed)
    {
        lock (_gate)
        {
            _lastFailure = (_clock.GetUtcNow(), bothFailed);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// 卡上的几行字，时间按本机时区：今天的只写钟点，更早的带上日期。
    /// </summary>
    public IReadOnlyList<string> Describe(TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var now = _clock.GetUtcNow();
        (DateTimeOffset At, FreeEngineName Engine)? success;
        (DateTimeOffset At, bool BothFailed)? failure;
        lock (_gate)
        {
            success = _lastSuccess;
            failure = _lastFailure;
        }

        var lines = new List<string>();
        if (success is null && failure is null)
        {
            lines.Add("这次启动以来还没用免费引擎翻译过。");
        }

        if (success is { } ok)
        {
            lines.Add($"上次成功：{When(ok.At, now, zone)} · {NameOf(ok.Engine)}");
        }

        if (failure is { } bad)
        {
            lines.Add(bad.BothFailed
                ? $"上次失败：{When(bad.At, now, zone)} · 微软和腾讯都没有响应"
                : $"上次失败：{When(bad.At, now, zone)} · 微软必应没有响应，改用了腾讯");
        }

        lines.Add(_health.BingRetryAt is { } retry && retry > now
            ? $"现在用：{NameOf(FreeEngineName.Tencent)}（{When(retry, now, zone)} 起再试微软）"
            : $"现在用：{NameOf(FreeEngineName.Bing)}，不可用时自动改用腾讯");
        return lines;
    }

    public static string NameOf(FreeEngineName engine) => engine switch
    {
        FreeEngineName.Bing => "微软必应",
        _ => "腾讯交互翻译",
    };

    private static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return local.Date == TimeZoneInfo.ConvertTime(now, zone).Date
            ? local.ToString("HH':'mm", CultureInfo.InvariantCulture)
            : local.ToString("M'月'd'日' HH':'mm", CultureInfo.InvariantCulture);
    }
}
