using Shiyu.Core;

namespace Shiyu.Core.Tests.Fakes;

/// <summary>
/// A clipboard and keyboard the test drives by hand, so the capture timing can
/// be examined without a real selection in a real application.
/// </summary>
public sealed class FakeCapturePlatform : ICapturePlatform
{
    private string? _clipboard;
    private uint _sequence;
    private int _waits;

    /// <summary>How many polls the target takes to answer. Null means never.</summary>
    public int? AnswersAfterPolls { get; set; }

    /// <summary>What the target puts on the clipboard when it answers.</summary>
    public string? Answer { get; set; } = "the selected text";

    public bool ReadFails { get; set; }
    public bool WriteFails { get; set; }
    public bool WriteThrows { get; set; }

    public List<string?> Writes { get; } = [];
    public int CopyKeystrokes { get; private set; }
    public int PasteKeystrokes { get; private set; }
    public int Polls { get; private set; }

    /// <summary>先后发生的事：wait、read、write、copy、paste——给需要断言次序的测试看。</summary>
    public List<string> Events { get; } = [];

    public void PutOnClipboard(string? text)
    {
        _clipboard = text;
        _sequence++;
    }

    public string? CurrentClipboard => _clipboard;

    public uint ClipboardSequenceNumber()
    {
        Polls++;
        return _sequence;
    }

    public string? ReadClipboardText()
    {
        Events.Add("read");
        return ReadFails ? throw new ClipboardUnavailableException("read failed") : _clipboard;
    }

    public bool WriteClipboardText(string? text)
    {
        if (WriteThrows)
        {
            throw new ClipboardUnavailableException("write failed");
        }

        Events.Add("write");
        Writes.Add(text);
        if (WriteFails)
        {
            return false;
        }

        _clipboard = text;
        _sequence++;
        return true;
    }

    /// <summary>用户的手指在修饰键上停留多少次查询（热键的 Ctrl、Shift……）；0 = 一开始就没按着。</summary>
    public int ModifiersHeldForChecks { get; set; }

    public int ModifierChecks { get; private set; }

    /// <summary>第一次发键之前等过几回（-1 = 还没发过键）。</summary>
    public int WaitsBeforeFirstKeystroke { get; private set; } = -1;

    /// <summary>第一次发键之前写过几回剪贴板（-1 = 还没发过键）。</summary>
    public int WritesBeforeFirstKeystroke { get; private set; } = -1;

    public bool ModifiersHeld() => ModifierChecks++ < ModifiersHeldForChecks;

    public void SendCopyKeystroke()
    {
        NoteKeystroke();
        Events.Add("copy");
        CopyKeystrokes++;
    }

    public void SendPasteKeystroke()
    {
        NoteKeystroke();
        Events.Add("paste");
        PasteKeystrokes++;
    }

    private void NoteKeystroke()
    {
        if (WaitsBeforeFirstKeystroke < 0)
        {
            WaitsBeforeFirstKeystroke = _waits;
            WritesBeforeFirstKeystroke = Writes.Count;
        }
    }

    public void Wait(TimeSpan duration)
    {
        Events.Add("wait");
        _waits++;

        // The target application "responds" once the agreed number of polls has
        // gone by, which is how a slow application is simulated.
        if (AnswersAfterPolls is { } threshold && _waits == threshold)
        {
            _clipboard = Answer;
            _sequence++;
        }
    }
}
