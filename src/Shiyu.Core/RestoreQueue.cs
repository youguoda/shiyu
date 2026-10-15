namespace Shiyu.Core;

/// <summary>一笔还原的结局，交给 App 决定要不要转告用户。</summary>
public sealed record SettledRestore(DeferredCapture Debt, CaptureRestore Result);

/// <summary>
/// 这一回还到了哪儿：结清了哪几笔；还有没还上的，<see cref="RetryAfter"/> 之后再来
/// （null：都还清了，或已经约好了下一回）。
/// </summary>
public sealed record RestoreProgress(IReadOnlyList<SettledRestore> Settled, TimeSpan? RetryAfter);

/// <summary>
/// 划词取词欠下的还原，按账本（<see cref="SelectionDebt"/>）交来的次序一笔一笔还（用户实录
/// 2026-10-15：开着拖选徽标时常弹「取词后未能还原你原本的剪贴板内容」）。取词刚写过剪贴板，
/// Windows 剪贴板历史、输入法、别的剪贴板工具正抢着读它；还原撞上"被占着"
/// （<see cref="CaptureRestore.Busy"/>）不判失败，隔一会儿再试（<see cref="CaptureTiming.RestoreRetryDelays"/>），
/// 试满了还不行才算还不上。
///
/// 次序是这里的要点：后到的债排在没还上的旧债后面。退出时账本先交新债、旧债压轴；新债若越过
/// 没还上的旧债先还，旧债再看剪贴板就认不出自己留下的那段字，当成用户复制过而销账——最后留在
/// 剪贴板上的就不是用户的原文。同理，还有债没还上（<see cref="Owing"/>）时，拖选不该再借。
/// </summary>
public sealed class RestoreQueue(Func<DeferredCapture, CaptureRestore> restore, IReadOnlyList<TimeSpan> retryDelays)
{
    private readonly Queue<DeferredCapture> _owed = new();

    /// <summary>排头那笔已经撞上几回"被占着"。</summary>
    private int _busy;

    /// <summary>还有没还上的债，在等下一回重试。</summary>
    public bool Owing => _owed.Count > 0;

    /// <summary>
    /// 账本交来一笔到期的债。前面没有在等的就当场还；有，就排在它们后面，到时一起还。
    /// </summary>
    public RestoreProgress Owe(DeferredCapture debt)
    {
        var waiting = Owing;
        _owed.Enqueue(debt);
        return waiting ? new RestoreProgress([], RetryAfter: null) : Pay();
    }

    /// <summary>约好的时候到了：从排头接着还。</summary>
    public RestoreProgress Retry() => Pay();

    /// <summary>
    /// 退出：没有下一回定时器了，剩下的债当场还完——撞上"被占着"就在这里等（<paramref name="wait"/>），
    /// 等满剩下的重试间隔还不行，才算还不上。
    /// </summary>
    public IReadOnlyList<SettledRestore> Drain(Action<TimeSpan> wait)
    {
        var settled = new List<SettledRestore>();
        while (true)
        {
            var progress = Pay();
            settled.AddRange(progress.Settled);
            if (progress.RetryAfter is not { } delay)
            {
                return settled;
            }

            wait(delay);
        }
    }

    private RestoreProgress Pay()
    {
        var settled = new List<SettledRestore>();
        while (_owed.TryPeek(out var debt))
        {
            var result = restore(debt);
            if (result == CaptureRestore.Busy && _busy < retryDelays.Count)
            {
                return new RestoreProgress(settled, retryDelays[_busy++]);
            }

            _owed.Dequeue();
            _busy = 0;
            settled.Add(new SettledRestore(debt, result));
        }

        return new RestoreProgress(settled, RetryAfter: null);
    }
}
