using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 回贴的次序（票 43）：快照 → 写入译文（拾语作为 owner，监听按 owner 跳过它）→ 记下写入后的序列号 →
/// 发 Ctrl+V → 400ms 后凭序列号决定还不还原。写入与还原都经同一个端口，所以"不进历史、不弹徽标"
/// 的保证落在端口的实现上（见 ClipboardWriteGateTests 与探针）。
/// </summary>
public class ReversePasteTests
{
    private const string Original = "原来的剪贴板";

    /// <summary>剪贴板与键盘共用一份调用记录，次序一眼可见。</summary>
    private sealed class Rig
    {
        public List<string> Calls { get; } = [];

        public Rig()
        {
            Clipboard = new FakeClipboard(Calls);
            Platform = new LoggingPlatform(Calls);
            Paste = new ReversePaste(Clipboard, new SelectionCapture(Platform));
        }

        public FakeClipboard Clipboard { get; }

        public LoggingPlatform Platform { get; }

        public ReversePaste Paste { get; }
    }

    private sealed class FakeClipboard(List<string> calls) : IReversePasteClipboard
    {
        public ClipboardBackup? Snapshot { get; set; } = new(
            Original, null, null, [], Excluded: false, LostFormats: [], Empty: false);

        public bool WriteFails { get; set; }
        public bool RestoreFails { get; set; }
        public string? Current { get; private set; } = Original;
        public uint Sequence { get; private set; } = 10;
        public List<ClipboardBackup> Restored { get; } = [];

        public ClipboardBackup? Backup()
        {
            calls.Add("backup");
            return Snapshot;
        }

        public bool Write(string text)
        {
            calls.Add("write");
            if (WriteFails)
            {
                return false;
            }

            Current = text;
            Sequence++;
            return true;
        }

        public uint SequenceNumber()
        {
            calls.Add("seq");
            return Sequence;
        }

        public bool Restore(ClipboardBackup backup)
        {
            calls.Add("restore");
            if (RestoreFails)
            {
                return false;
            }

            Restored.Add(backup);
            Current = backup.Text;
            Sequence++;
            return true;
        }

        /// <summary>用户（或别的程序）在拾语写入之后复制了别的东西。</summary>
        public void SomeoneCopies(string text)
        {
            Current = text;
            Sequence++;
        }
    }

    private sealed class LoggingPlatform(List<string> calls) : ICapturePlatform
    {
        public bool PasteThrows { get; set; }

        public uint ClipboardSequenceNumber() => 0;

        public string? ReadClipboardText() => null;

        public bool WriteClipboardText(string? text) => true;

        public void SendCopyKeystroke() { }

        public void SendPasteKeystroke()
        {
            calls.Add("paste");
            if (PasteThrows)
            {
                throw new ClipboardUnavailableException("cannot send");
            }
        }

        public void Wait(TimeSpan duration) { }
    }

    [Fact]
    public void The_order_is_backup_then_write_then_sequence_then_the_keystroke()
    {
        var rig = new Rig();

        rig.Paste.Send("[EN] 你好");

        // 快照必须在写入之前——写入就把用户的剪贴板覆盖了；序列号必须紧跟在写入之后、
        // 发键之前——键发出去以后别的程序的写入就混进来了。
        Assert.Equal(["backup", "write", "seq", "paste"], rig.Calls);
    }

    [Fact]
    public void A_successful_paste_carries_what_the_later_restore_needs()
    {
        var rig = new Rig();

        var result = rig.Paste.Send("[EN] 你好");

        Assert.Equal(ReversePasteOutcome.Pasted, result.Outcome);
        var pending = Assert.IsType<PendingRestore>(result.Pending);
        Assert.Equal(rig.Clipboard.Snapshot, pending.Backup);
        Assert.Equal(rig.Clipboard.Sequence, pending.SequenceAfterWrite);
        Assert.Equal("[EN] 你好", rig.Clipboard.Current);
    }

    [Fact]
    public void The_clipboard_is_put_back_when_nothing_touched_it_meanwhile()
    {
        var rig = new Rig();
        var pending = rig.Paste.Send("[EN] 你好").Pending!;

        var settled = rig.Paste.Settle(pending);

        Assert.Equal(RestoreVerdict.Restore, settled.Verdict);
        Assert.True(settled.Restored);
        Assert.Equal(Original, rig.Clipboard.Current);
    }

    [Fact]
    public void A_copy_made_in_the_meantime_is_not_overwritten()
    {
        var rig = new Rig();
        var pending = rig.Paste.Send("[EN] 你好").Pending!;
        rig.Clipboard.SomeoneCopies("用户刚复制的");

        var settled = rig.Paste.Settle(pending);

        Assert.Equal(RestoreVerdict.SkipChangedSinceWrite, settled.Verdict);
        Assert.False(settled.Restored);
        Assert.Equal("用户刚复制的", rig.Clipboard.Current);
        Assert.Empty(rig.Clipboard.Restored);
    }

    [Fact]
    public void An_excluded_snapshot_is_never_written_back()
    {
        var rig = new Rig();
        rig.Clipboard.Snapshot = new ClipboardBackup(null, null, null, [], Excluded: true, [], Empty: false);
        var pending = rig.Paste.Send("[EN] 你好").Pending!;

        var settled = rig.Paste.Settle(pending);

        Assert.Equal(RestoreVerdict.SkipExcluded, settled.Verdict);
        Assert.Empty(rig.Clipboard.Restored);
        Assert.Equal("[EN] 你好", rig.Clipboard.Current);
    }

    [Fact]
    public void A_snapshot_with_an_image_is_left_to_the_history()
    {
        var rig = new Rig();
        rig.Clipboard.Snapshot = new ClipboardBackup(null, null, null, [], false, ["CF_DIB"], Empty: false);
        var pending = rig.Paste.Send("[EN] 你好").Pending!;

        var settled = rig.Paste.Settle(pending);

        Assert.Equal(RestoreVerdict.SkipUnrestorableFormat, settled.Verdict);
        Assert.Empty(rig.Clipboard.Restored);
    }

    [Fact]
    public void An_empty_clipboard_is_put_back_empty()
    {
        var rig = new Rig();
        rig.Clipboard.Snapshot = new ClipboardBackup(null, null, null, [], false, [], Empty: true);
        var pending = rig.Paste.Send("[EN] 你好").Pending!;

        var settled = rig.Paste.Settle(pending);

        Assert.True(settled.Restored);
        Assert.True(Assert.Single(rig.Clipboard.Restored).Empty);
    }

    [Fact]
    public void A_restore_that_does_not_take_is_reported_not_swallowed()
    {
        var rig = new Rig();
        var pending = rig.Paste.Send("[EN] 你好").Pending!;
        rig.Clipboard.RestoreFails = true;

        var settled = rig.Paste.Settle(pending);

        Assert.Equal(RestoreVerdict.Restore, settled.Verdict);
        Assert.False(settled.Restored);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_read_still_pastes_but_has_nothing_to_put_back()
    {
        var rig = new Rig();
        rig.Clipboard.Snapshot = null;

        var result = rig.Paste.Send("[EN] 你好");

        Assert.Equal(ReversePasteOutcome.Pasted, result.Outcome);
        Assert.Null(result.Pending);
        Assert.Contains("paste", rig.Calls);
    }

    [Fact]
    public void A_write_that_fails_sends_no_keystroke_and_tries_to_put_the_old_clipboard_back()
    {
        var rig = new Rig();
        rig.Clipboard.WriteFails = true;

        var result = rig.Paste.Send("[EN] 你好");

        // 写入器是先清空再写的：失败时用户的剪贴板可能已经空了——能还的立刻还回去。
        Assert.Equal(ReversePasteOutcome.ClipboardUnavailable, result.Outcome);
        Assert.Null(result.Pending);
        Assert.DoesNotContain("paste", rig.Calls);
        Assert.Single(rig.Clipboard.Restored);
    }

    [Fact]
    public void A_write_that_fails_does_not_try_to_restore_what_must_not_be_restored()
    {
        var rig = new Rig();
        rig.Clipboard.Snapshot = new ClipboardBackup("hunter2", null, null, [], Excluded: true, [], Empty: false);
        rig.Clipboard.WriteFails = true;

        rig.Paste.Send("[EN] 你好");

        Assert.Empty(rig.Clipboard.Restored);
    }

    [Fact]
    public void A_keystroke_that_cannot_be_sent_leaves_the_translation_on_the_clipboard_for_a_manual_paste()
    {
        var rig = new Rig();
        rig.Platform.PasteThrows = true;

        var result = rig.Paste.Send("[EN] 你好");

        Assert.Equal(ReversePasteOutcome.KeystrokeFailed, result.Outcome);
        Assert.Null(result.Pending);
        Assert.Equal("[EN] 你好", rig.Clipboard.Current);
    }

    [Fact]
    public void When_the_way_back_to_the_window_is_lost_the_text_is_left_on_the_clipboard_without_a_keystroke()
    {
        // 回不到原窗口时不敢发 Ctrl+V（会贴进别的窗口）：只写进剪贴板，让用户手动贴；
        // 不还原——此刻用户要的恰恰是它。
        var rig = new Rig();

        Assert.True(rig.Paste.Leave("[EN] 你好"));

        Assert.Equal(["write"], rig.Calls);
        Assert.Equal("[EN] 你好", rig.Clipboard.Current);
    }

    [Fact]
    public void Leaving_a_blank_text_touches_nothing_and_a_failed_write_says_so()
    {
        var rig = new Rig();
        Assert.False(rig.Paste.Leave("  "));
        Assert.Empty(rig.Calls);

        rig.Clipboard.WriteFails = true;
        Assert.False(rig.Paste.Leave("[EN] 你好"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_text_is_not_pasted_and_the_clipboard_is_not_touched(string text)
    {
        var rig = new Rig();

        var result = rig.Paste.Send(text);

        Assert.Equal(ReversePasteOutcome.NothingToPaste, result.Outcome);
        Assert.Empty(rig.Calls);
    }
}
