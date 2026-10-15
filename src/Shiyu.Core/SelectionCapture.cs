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

    /// <summary>
    /// 借走之前给剪贴板拍的快照：文本、HTML、RTF、文件列表，外加写不回去的格式与排除标记
    /// （与反向输入框回贴同一份 <see cref="ClipboardBackup"/>）。剪贴板被别的程序占着、读不了
    /// 时给 null。
    /// </summary>
    ClipboardBackup? Backup();

    /// <summary>把快照写回去。剪贴板被别的程序占着、没写上时给 false。</summary>
    bool Restore(ClipboardBackup backup);

    /// <summary>
    /// 剪贴板上最后一笔是不是拾语自己写的（owner 是拾语的窗口）。和序列号一样，问它不用打开剪贴板。
    /// </summary>
    bool OwnsClipboard();

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

/// <param name="Restore">What became of the user's own clipboard (用户实录 2026-10-15).</param>
public sealed record CaptureResult(string? Text, CaptureOutcome Outcome, CaptureRestore Restore)
{
    public bool Succeeded => Outcome == CaptureOutcome.Captured;

    /// <summary>
    /// Whether the user's own clipboard survived — put back, never touched,
    /// or (a password) deliberately left alone. False here is the one failure
    /// the user would genuinely resent, so callers must surface it.
    /// </summary>
    public bool ClipboardRestored => Restore is not (CaptureRestore.Busy or CaptureRestore.Unrestorable);
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

    /// <summary>
    /// 还原撞上剪贴板被占着（<see cref="CaptureRestore.Busy"/>）之后，隔多久再试、试几回（用户实录
    /// 2026-10-15：开着拖选徽标时常弹「取词后未能还原你原本的剪贴板内容」）。取词刚写过剪贴板，
    /// Windows 剪贴板历史、别的剪贴板工具、还在补写格式的浏览器都会去开它；写入器自己只重试约
    /// 120 毫秒，撞上就判失败太急。逐次拉长，合计不到两秒。
    /// </summary>
    public IReadOnlyList<TimeSpan> RestoreRetryDelays { get; init; } =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000),
    ];
}

/// <summary>一次还原的结局（用户实录 2026-10-15：开着拖选徽标时常弹「未能还原」）。</summary>
public enum CaptureRestore
{
    /// <summary>用户原来的剪贴板写回去了。</summary>
    Restored,

    /// <summary>剪贴板从借走起就没动过（Ctrl+C 没取到东西）：什么都不用写。</summary>
    Untouched,

    /// <summary>借走之后用户自己复制过：不去踩掉更新的内容，这笔债销了。</summary>
    Superseded,

    /// <summary>
    /// 原来是带「勿记录」标记的内容（密码管理器复制的密码）：故意不写回——写回时标记会丢，
    /// Windows 剪贴板历史就会把它记下来。剪贴板上留着取到的文字。
    /// </summary>
    Withheld,

    /// <summary>
    /// 原来的内容写不回去（截图之类，没有一点文字或文件；或剪切的文件）：没写，剪贴板上留着取到的
    /// 文字。见 <see cref="ClipboardRestore.CaptureVerdict"/>。
    /// </summary>
    Unrestorable,

    /// <summary>剪贴板被别的程序占着，这一次没写上：稍后再试（<see cref="CaptureTiming.RestoreRetryDelays"/>）。</summary>
    Busy,
}

/// <summary>
/// 划词路径的取词结果（票 37）：文字取到了，剪贴板还攥在拾语手里——还原
/// 被推迟到翻译面板显示之后（采纳 Glossy 的次序细节：还原可以等，被复制
/// 的应用可能还要用；而点击到出面板这一段不该再添一次剪贴板写）。
/// 调用方负责在面板显示之后或徽标淡出时调用 <see cref="SelectionCapture.Restore"/>
/// 把 <see cref="Backup"/> 还回去，且只还一次。
/// </summary>
/// <param name="Backup">借走之前的剪贴板快照（多格式，用户实录 2026-10-15 之前只存了文字）。</param>
/// <param name="SequenceBefore">
/// 借走时的剪贴板序列号。还原时序列号还是它，说明剪贴板根本没动过，什么都不用写。
/// </param>
/// <param name="SequenceSealed">
/// 取到文字、以拾语名义写回之后的序列号。还原时序列号不是它，说明之后有人（用户）写过
/// 剪贴板，这笔债就销了。没取到文字、要立刻还的，没有封存，为 null。
/// 判断全凭序列号：读它不用打开剪贴板，别的程序占着剪贴板时照样判得准。
/// </param>
public sealed record DeferredCapture(
    string? Text,
    CaptureOutcome Outcome,
    ClipboardBackup Backup,
    uint SequenceBefore,
    uint? SequenceSealed)
{
    public bool Succeeded => Outcome == CaptureOutcome.Captured;

    /// <summary>借走的那份里的文字（日志与测试辨认用）。</summary>
    public string? Borrowed => Backup.Text;
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

        // 热键是用户点名要的取词：剪贴板里有写不回去的东西也照取，结局如实报告。
        if (Borrow(requireRestorable: false) is not { } borrowed)
        {
            // Nothing was borrowed, so there is nothing to put back.
            return new CaptureResult(null, CaptureOutcome.ClipboardUnavailable, CaptureRestore.Untouched);
        }

        var (captured, outcome) = CopySelection(borrowed.Before);

        // After every path above — each of them may have taken the user's
        // clipboard away, including the ones that threw. Not sealed: in these
        // few milliseconds nothing but the target answering us has written,
        // so there is no user copy to protect.
        var restore = RestoreRetrying(new DeferredCapture(
            outcome == CaptureOutcome.Captured ? captured : null,
            outcome,
            borrowed.Backup,
            borrowed.Before,
            SequenceSealed: null));

        return new CaptureResult(outcome == CaptureOutcome.Captured ? captured : null, outcome, restore);
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
    /// <param name="requireRestorable">
    /// 拖选这种被动的取词传 true：剪贴板里的东西写不回去——截图、剪切的文件、带「勿记录」
    /// 标记的密码（<see cref="ClipboardRestore.CaptureVerdict"/>）——就不借、不发按键（用户实录
    /// 2026-10-15）。被动遭遇不该让用户赔上剪贴板。
    /// </param>
    /// <returns>
    /// null 表示没借：剪贴板读不了，或（<paramref name="requireRestorable"/>）里面的东西写不回去
    /// ——没有发过按键，也没有需要还原的东西。
    /// </returns>
    public DeferredCapture? CaptureDeferRestore(bool requireRestorable = false)
    {
        AwaitModifiersReleased();

        if (Borrow(requireRestorable) is not { } borrowed)
        {
            return null;
        }

        var (captured, outcome) = CopySelection(borrowed.Before);

        uint? sealedAt = null;
        if (outcome == CaptureOutcome.Captured)
        {
            try
            {
                // 名义写回失败不是取词失败——只是少了自我抑制的保险。
                platform.WriteClipboardText(captured);
            }
            catch (ClipboardUnavailableException)
            {
                // expected: 别的程序占着剪贴板；取到的文字照样在手里。
            }

            // 写没写上都记下此刻的序列号：之后它再动，就是别人（用户）写了剪贴板。
            sealedAt = platform.ClipboardSequenceNumber();
        }

        return new DeferredCapture(
            outcome == CaptureOutcome.Captured ? captured : null,
            outcome,
            borrowed.Backup,
            borrowed.Before,
            sealedAt);
    }

    /// <summary>
    /// 把借走的剪贴板还回去（用户实录 2026-10-15：开着拖选徽标时常弹「取词后未能还原你原本的
    /// 剪贴板内容」）。要不要写、写什么全凭序列号与快照判断，判断时<b>不打开剪贴板</b>——原来要先
    /// 读一遍文字作比较，别的程序正占着剪贴板时这一读就失败，于是报"未能还原"：
    /// <list type="bullet">
    /// <item>序列号还是借走时那个：剪贴板根本没动过（Ctrl+C 没取到东西），什么都不写；</item>
    /// <item>封存之后序列号又动了、最后一笔不是拾语写的：用户自己复制过（哪怕是同一段字），不去
    /// 踩掉更新的内容，这笔债就销了；</item>
    /// <item>快照带「勿记录」标记、或里面没有写得回去的东西（<see cref="ClipboardRestore.CaptureVerdict"/>）：
    /// 不写，结局分别是 Withheld / Unrestorable；</item>
    /// <item>否则把快照写回去——文本连同 HTML、RTF，或文件列表，原来是空的就清空；附带的图元文件、
    /// 位图之类写不回去的格式丢掉。</item>
    /// </list>
    /// 写不上（剪贴板被占着）给 <see cref="CaptureRestore.Busy"/>：调用方隔一会儿再试
    /// （<see cref="CaptureTiming.RestoreRetryDelays"/>），再判一次——期间用户复制了别的，下一次就是
    /// Superseded。
    /// </summary>
    public CaptureRestore Restore(DeferredCapture deferred)
    {
        var now = platform.ClipboardSequenceNumber();
        if (now == deferred.SequenceBefore)
        {
            return CaptureRestore.Untouched;
        }

        if (deferred.SequenceSealed is { } sealedAt && now != sealedAt)
        {
            // 封存之后有人写过剪贴板。不是拾语写的，就是用户复制了——哪怕复制的正是这段选中文字
            // （选中之后顺手按 Ctrl+C，再常见不过；原来比文字，一样就还原，把用户这一下复制撤掉了）：
            // 不去踩它，这笔债销了。
            if (!platform.OwnsClipboard())
            {
                return CaptureRestore.Superseded;
            }

            // 是拾语写的：多半是后一笔取词借走了我们留下的那段字、又把它还了回来（退出时新债先还、
            // 旧债压轴）。只有这种少见的情况才打开剪贴板读一读：还是我们留下的那段字就照还；别的
            // （从历史里重新复制了一条）同样销账。
            string? current;
            try
            {
                current = platform.ReadClipboardText();
            }
            catch (ClipboardUnavailableException)
            {
                return CaptureRestore.Busy;
            }

            if (current != deferred.Text)
            {
                return CaptureRestore.Superseded;
            }
        }

        switch (ClipboardRestore.CaptureVerdict(deferred.Backup))
        {
            case RestoreVerdict.SkipExcluded:
                return CaptureRestore.Withheld;

            case RestoreVerdict.SkipUnrestorableFormat:
                return CaptureRestore.Unrestorable;
        }

        try
        {
            return platform.Restore(deferred.Backup) ? CaptureRestore.Restored : CaptureRestore.Busy;
        }
        catch (ClipboardUnavailableException)
        {
            return CaptureRestore.Busy;
        }
    }

    /// <summary>
    /// 同步的还原：撞上 Busy 就按 <see cref="CaptureTiming.RestoreRetryDelays"/> 等一等再试。热键那条路
    /// 用它——取词本来就在等，多等这一点点好过把用户的剪贴板丢在半路。
    /// </summary>
    private CaptureRestore RestoreRetrying(DeferredCapture deferred)
    {
        var result = Restore(deferred);
        foreach (var delay in _timing.RestoreRetryDelays)
        {
            if (result != CaptureRestore.Busy)
            {
                break;
            }

            platform.Wait(delay);
            result = Restore(deferred);
        }

        return result;
    }

    /// <summary>
    /// 借出前的快照：剪贴板打不开就没借到任何东西——两种取词在这里分道，一个报"不可用"，
    /// 一个连债都没有。<paramref name="requireRestorable"/> 时，写不回去的也不借。
    /// </summary>
    private (ClipboardBackup Backup, uint Before)? Borrow(bool requireRestorable)
    {
        ClipboardBackup? backup;
        try
        {
            backup = platform.Backup();
        }
        catch (ClipboardUnavailableException)
        {
            backup = null;
        }

        if (backup is null
            || (requireRestorable && ClipboardRestore.CaptureVerdict(backup) != RestoreVerdict.Restore))
        {
            return null;
        }

        return (backup, platform.ClipboardSequenceNumber());
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
}

/// <summary>Raised when the clipboard could not be opened at all.</summary>
public sealed class ClipboardUnavailableException(string message) : Exception(message);
