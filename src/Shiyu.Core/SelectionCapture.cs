namespace Shiyu.Core;

/// <summary>
/// The operating system operations capture needs. Behind a port because the
/// timing logic around them is the riskiest code in Shiyu and has to be
/// testable without a real keyboard.
/// </summary>
public interface ICapturePlatform
{
    /// <summary>
    /// Changes whenever anything writes to the clipboard. This is how capture
    /// learns the target application has answered: nothing tells us directly.
    /// </summary>
    uint ClipboardSequenceNumber();

    string? ReadClipboardText();

    /// <summary>Writes text back, or clears the clipboard when given null.</summary>
    bool WriteClipboardText(string? text);

    void SendCopyKeystroke();

    void SendPasteKeystroke();

    /// <summary>
    /// 用户此刻还按着 Ctrl / Shift / Alt / Win 吗。热键在按下的那一刻就触发，手指还在热键的修饰键上；
    /// 发键之前要等它们离开（<see cref="CaptureTiming.ModifierRelease"/>）。
    /// </summary>
    bool ModifiersHeld();

    /// <summary>Explicit so tests can drive the polling loop deterministically.</summary>
    void Wait(TimeSpan duration);
}

public enum CaptureOutcome
{
    /// <summary>Text was captured.</summary>
    Captured,

    /// <summary>
    /// Nothing arrived before the deadline. An empty selection and an
    /// unresponsive application are indistinguishable from out here — in both
    /// cases the clipboard simply never changed — so they share one outcome
    /// rather than pretending to a certainty we do not have.
    /// </summary>
    NothingCaptured,

    /// <summary>The clipboard could not be read or written at all.</summary>
    ClipboardUnavailable,
}

/// <param name="ClipboardRestored">
/// Whether the user's own clipboard survived. False here is the one failure
/// the user would genuinely resent, so callers must surface it.
/// </param>
public sealed record CaptureResult(string? Text, CaptureOutcome Outcome, bool ClipboardRestored)
{
    public bool Succeeded => Outcome == CaptureOutcome.Captured;
}

public sealed record CaptureTiming(TimeSpan PollInterval, TimeSpan Timeout)
{
    /// <summary>
    /// Polling every 15 ms for up to 600 ms. Too short and slow applications —
    /// Electron, remote desktops, anything busy — never get their answer in;
    /// too long and a failed capture feels like the tool has hung. These are
    /// starting values, meant to be revisited against real applications.
    /// </summary>
    public static CaptureTiming Default { get; } =
        new(TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(600));

    /// <summary>
    /// 发键之前最多等手指离开修饰键多久。按完热键通常一两百毫秒就松开；有人就是按着不放，
    /// 也不能让取词一直悬着——过了这个上限照发（平台借用他按着的 Ctrl，不替他松开）。
    /// </summary>
    public TimeSpan ModifierRelease { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// 划词路径的取词结果（票 37）：文字取到了，剪贴板还攥在拾语手里——还原
/// 被推迟到翻译面板显示之后（采纳 Glossy 的次序细节：还原可以等，被复制
/// 的应用可能还要用；而点击到出面板这一段不该再添一次剪贴板写）。
/// 调用方负责在面板显示之后或徽标淡出时调用 <see cref="SelectionCapture.Restore"/>
/// 把 <see cref="Borrowed"/> 还回去，且只还一次。
/// </summary>
public sealed record DeferredCapture(string? Text, CaptureOutcome Outcome, string? Borrowed)
{
    public bool Succeeded => Outcome == CaptureOutcome.Captured;
}

/// <summary>
/// Borrows the clipboard to read the user's selection, then puts it back.
///
/// There is no notification when a target application finishes writing to the
/// clipboard, so this polls a sequence number until it moves or the deadline
/// passes. The one rule that outranks everything else: whatever happens, the
/// clipboard the user had is restored.
/// </summary>
public sealed class SelectionCapture(ICapturePlatform platform, CaptureTiming? timing = null)
{
    private readonly CaptureTiming _timing = timing ?? CaptureTiming.Default;

    public CaptureResult Capture()
    {
        AwaitModifiersReleased();

        if (!TryBorrow(out var borrowed, out var before))
        {
            // Nothing was borrowed, so there is nothing to put back.
            return new CaptureResult(null, CaptureOutcome.ClipboardUnavailable, ClipboardRestored: true);
        }

        var (captured, outcome) = CopySelection(before);

        // Unconditional, and after every path above — each of them has already
        // taken the user's clipboard away, including the ones that threw.
        var restored = WriteBack(borrowed);

        return new CaptureResult(
            outcome == CaptureOutcome.Captured ? captured : null,
            outcome,
            restored);
    }

    /// <summary>
    /// 划词路径的取词：借出 → 模拟 Ctrl+C → 等答案，但<b>不还原</b>——还原
    /// 是调用方的事，时序见 <see cref="DeferredCapture"/>。
    ///
    /// 取到文字后，会把它立即以拾语自己的名义写回剪贴板。两个作用：剪贴板
    /// 此刻的 owner 是拾语，"目标应用完成复制"的迟到通知在还原前抵达也会
    /// 被自我抑制挡下（监控按<b>处理时刻</b>的 owner 判定，见
    /// WindowsClipboardMonitor）；用户在徽标期间的真复制不受影响——那些写
    /// 的 owner 是用户自己的应用。
    /// </summary>
    /// <returns>
    /// null 表示借都没借到（读剪贴板就失败了）——没有发过按键，也没有
    /// 需要还原的东西。
    /// </returns>
    public DeferredCapture? CaptureDeferRestore()
    {
        AwaitModifiersReleased();

        if (!TryBorrow(out var borrowed, out var before))
        {
            return null;
        }

        var (captured, outcome) = CopySelection(before);

        if (outcome == CaptureOutcome.Captured)
        {
            try
            {
                // 名义写回失败不是取词失败——只是少了自我抑制的保险。
                platform.WriteClipboardText(captured);
            }
            catch (ClipboardUnavailableException)
            {
            }
        }

        return new DeferredCapture(
            outcome == CaptureOutcome.Captured ? captured : null,
            outcome,
            borrowed);
    }

    /// <summary>
    /// 把划词路径借走的剪贴板还回去，且只在剪贴板<b>仍是我们留下的那份选中
    /// 文字</b>时才还：借出之后用户若自己复制过，还原就会踩掉更新的内容——
    /// 那比不还更糟。剪贴板已经往前走了，这笔债就销了，原样保留用户的。
    /// 失败（想还而没还成）如实上报，由调用方转告用户。
    /// </summary>
    public bool Restore(DeferredCapture deferred)
    {
        try
        {
            if (deferred.Succeeded && platform.ReadClipboardText() != deferred.Text)
            {
                // 债已销：剪贴板里已不是我们放的东西，别碰它。
                return true;
            }

            return WriteBack(deferred.Borrowed);
        }
        catch (ClipboardUnavailableException)
        {
            return false;
        }
    }

    /// <summary>
    /// 借出前的一读：剪贴板打不开就没借到任何东西——两种取词在这里分道，
    /// 一个报"不可用"，一个连债都没有。
    /// </summary>
    private bool TryBorrow(out string? borrowed, out uint before)
    {
        try
        {
            borrowed = platform.ReadClipboardText();
            before = platform.ClipboardSequenceNumber();
            return true;
        }
        catch (ClipboardUnavailableException)
        {
            borrowed = null;
            before = 0;
            return false;
        }
    }

    /// <summary>
    /// Sends only the copy keystroke and waits for an answer. The side effects
    /// around it — what to borrow, when to restore — belong to the callers,
    /// which is what keeps the deferred variant honest.
    /// </summary>
    private (string? Captured, CaptureOutcome Outcome) CopySelection(uint before)
    {
        string? captured = null;
        var outcome = CaptureOutcome.NothingCaptured;

        try
        {
            platform.SendCopyKeystroke();

            for (var waited = TimeSpan.Zero; waited < _timing.Timeout; waited += _timing.PollInterval)
            {
                platform.Wait(_timing.PollInterval);

                if (platform.ClipboardSequenceNumber() == before)
                {
                    continue;
                }

                captured = platform.ReadClipboardText();

                // A changed clipboard holding nothing usable is still nothing
                // captured — never fall back to what was there before, which
                // would hand back stale content as if it were the selection.
                if (!string.IsNullOrEmpty(captured))
                {
                    outcome = CaptureOutcome.Captured;
                }

                break;
            }
        }
        catch (ClipboardUnavailableException)
        {
            outcome = CaptureOutcome.ClipboardUnavailable;
        }

        return (captured, outcome);
    }

    /// <summary>
    /// Puts text into whatever window was in front, for the quick bar and the
    /// paste half of capture.
    /// </summary>
    public bool Paste(string text)
    {
        AwaitModifiersReleased();

        try
        {
            if (!platform.WriteClipboardText(text))
            {
                return false;
            }

            platform.SendPasteKeystroke();
            return true;
        }
        catch (ClipboardUnavailableException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends only the paste keystroke, for callers that have already put a
    /// rich multi-format payload on the clipboard themselves.
    /// </summary>
    public bool PasteCurrentClipboard()
    {
        AwaitModifiersReleased();

        try
        {
            platform.SendPasteKeystroke();
            return true;
        }
        catch (ClipboardUnavailableException)
        {
            return false;
        }
    }

    /// <summary>
    /// 等手指离开修饰键再动（用户实录 2026-10-10：用过划词翻译以后，Ctrl 快捷键有时不灵）。热键在
    /// 按下的那一刻就触发，手指还在 Ctrl+Shift 上：这时发 Ctrl+C，得先替用户松开 Shift、最后还要
    /// 松开 Ctrl——他还按着的 Ctrl 在 Windows 眼里就没了，紧接着的 Ctrl+S 只打出一个 s。等他松开
    /// （通常一两百毫秒）再借剪贴板、再发键，就是一次干净的 Ctrl+C。一直不松也有上限
    /// （<see cref="CaptureTiming.ModifierRelease"/>），过了照发。
    /// </summary>
    private void AwaitModifiersReleased()
    {
        for (var waited = TimeSpan.Zero;
             waited < _timing.ModifierRelease && platform.ModifiersHeld();
             waited += _timing.PollInterval)
        {
            platform.Wait(_timing.PollInterval);
        }
    }

    private bool WriteBack(string? borrowed)
    {
        try
        {
            return platform.WriteClipboardText(borrowed);
        }
        catch (ClipboardUnavailableException)
        {
            return false;
        }
    }
}

/// <summary>Raised when the clipboard could not be opened at all.</summary>
public sealed class ClipboardUnavailableException(string message) : Exception(message);
