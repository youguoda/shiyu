namespace Shiyu.Core;

/// <summary>
/// 回贴时拾语对剪贴板做的四件事（票 43），Windows 层用 <c>WindowsClipboardWriter</c> 实现。
/// 写入与还原都经同一个写入器——拾语作为剪贴板 owner，监听按 owner 跳过它们：
/// <b>不产生历史条目，也不弹徽标</b>。
/// </summary>
public interface IReversePasteClipboard
{
    /// <summary>快照现在的剪贴板。读不了（别的进程占着）给 null。带排除标记的快照不读内容。</summary>
    ClipboardBackup? Backup();

    /// <summary>把文字写上剪贴板。写不上给 false。</summary>
    bool Write(string text);

    uint SequenceNumber();

    /// <summary>把快照写回去。写不回去给 false。</summary>
    bool Restore(ClipboardBackup backup);
}

/// <summary>等 <see cref="ReversePaste.RestoreDelay"/> 之后凭它决定还不还原。</summary>
public sealed record PendingRestore(ClipboardBackup Backup, uint SequenceAfterWrite);

public enum ReversePasteOutcome
{
    Pasted,

    /// <summary>没有可贴的文字，什么都没碰。</summary>
    NothingToPaste,

    /// <summary>写不上剪贴板。</summary>
    ClipboardUnavailable,

    /// <summary>译文已经在剪贴板上，但 Ctrl+V 没发出去：留着译文，用户可以手动贴。</summary>
    KeystrokeFailed,
}

/// <param name="Pending">只有贴出去了、而且有东西可还时才有。</param>
public readonly record struct ReversePasteResult(ReversePasteOutcome Outcome, PendingRestore? Pending);

/// <param name="Restored">真的写回去了。裁决是 Restore 而这里是 false，说明写回失败了。</param>
public readonly record struct SettleResult(RestoreVerdict Verdict, bool Restored);

/// <summary>
/// 反向输入框的回贴（票 43）：快照 → 写入译文 → 记序列号 → 发 Ctrl+V → 400ms 后有条件还原。
///
/// 前台窗口的恢复（<c>ForegroundWindow</c>）在它之前、由界面做；发键复用窄条粘贴模式那条路
/// （<see cref="SelectionCapture.PasteCurrentClipboard"/>——已经处理了修饰键残留）。
/// 窄条粘贴不还原，因为把内容留在剪贴板上是剪贴板管理器的本分；这里的结果只是"运输"，
/// 不该占掉用户原来的剪贴板。
/// </summary>
public sealed class ReversePaste(IReversePasteClipboard clipboard, SelectionCapture capture)
{
    /// <summary>
    /// 贴出去之后等多久才还原：等目标程序把剪贴板读走（取自 Xtranslate 的 400ms）。太短，
    /// 慢的应用会读到被还原的旧内容；实机若出现"贴出来的是旧内容"，加大它。
    /// </summary>
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(400);

    public ReversePasteResult Send(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ReversePasteResult(ReversePasteOutcome.NothingToPaste, null);
        }

        // 快照在写入之前：写入就把用户的剪贴板覆盖了。
        var backup = clipboard.Backup();

        if (!clipboard.Write(text))
        {
            // 写入器是先清空再写的：失败时用户的剪贴板可能已经空了。"不管发生什么，
            // 用户原来的剪贴板要还回去"（取词那条路的铁律），能还的立刻还。
            if (backup is not null
                && ClipboardRestore.Decide(backup, 0, 0) == RestoreVerdict.Restore)
            {
                clipboard.Restore(backup);
            }

            return new ReversePasteResult(ReversePasteOutcome.ClipboardUnavailable, null);
        }

        // 紧跟在写入之后、发键之前：键发出去以后，别的程序的写入就混进来了。
        var sequence = clipboard.SequenceNumber();

        if (!capture.PasteCurrentClipboard())
        {
            return new ReversePasteResult(ReversePasteOutcome.KeystrokeFailed, null);
        }

        return new ReversePasteResult(
            ReversePasteOutcome.Pasted,
            backup is null ? null : new PendingRestore(backup, sequence));
    }

    /// <summary>
    /// 回不到原窗口、不敢发 Ctrl+V 时（会贴进别的窗口）：只把译文写进剪贴板，让用户手动贴。
    /// 不还原——此刻用户要的恰恰是它。
    /// </summary>
    public bool Leave(string text) => !string.IsNullOrWhiteSpace(text) && clipboard.Write(text);

    /// <summary>
    /// <see cref="RestoreDelay"/> 之后调用：序列号没变才还原，带排除标记的、有写不回去的格式的不还。
    /// </summary>
    public SettleResult Settle(PendingRestore pending)
    {
        var verdict = ClipboardRestore.Decide(
            pending.Backup, pending.SequenceAfterWrite, clipboard.SequenceNumber());

        return verdict == RestoreVerdict.Restore
            ? new SettleResult(verdict, clipboard.Restore(pending.Backup))
            : new SettleResult(verdict, Restored: false);
    }
}
