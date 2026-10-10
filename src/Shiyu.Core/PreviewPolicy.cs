namespace Shiyu.Core;

/// <summary>What started the preview, which decides what may close it.</summary>
public enum PreviewTrigger
{
    /// <summary>Space is held down: it stays until the key comes up.</summary>
    Keyboard,

    /// <summary>Hover dwell: it survives the pointer's trip between cards.</summary>
    Hover,
}

/// <summary>What the policy wants the window to do after an event.</summary>
public enum PreviewCommand
{
    /// <summary>Nothing changed.</summary>
    None,

    /// <summary>Show the panel for a card, at its final size.</summary>
    Open,

    /// <summary>The same panel, carried to a neighbouring card — never closed and reopened.</summary>
    Retarget,

    /// <summary>Take the panel away.</summary>
    Close,
}

/// <summary>
/// When the preview panel opens, moves, and closes (ticket 17). The timings
/// are product decisions; they live here, tested, rather than in
/// DispatcherTimers scattered through a window class:
///
/// - Hover dwells before opening, so a pointer merely passing through does
///   not summon a panel.
/// - Leaving a card starts a grace buffer: the gap between two neighbouring
///   cards is crossed in milliseconds, and a preview that closes and reopens
///   there reads as flicker. Entering the next card inside the buffer
///   retargets instead.
/// - The pointer resting on the preview itself suspends the buffer — the
///   user is reading it.
/// - Keyboard moves wait for the selection to settle: chasing a target that
///   is still scrolling wobbles.
/// - Scrolling the list closes a hover preview (the user has moved on) but
///   not a held-space one (the key, not the scroll, owns it).
/// </summary>
public sealed class PreviewPolicy
{
    /// <summary>The grace period while crossing from one card to its neighbour.</summary>
    public const int HoverBufferMs = 240;

    /// <summary>How still the keyboard selection must be before the panel follows.</summary>
    public const int KeyboardSettleMs = 180;

    private readonly int _hoverDelayMs;

    private enum State
    {
        Closed,
        PendingHover,
        Open,
        Buffering,
        OverPreview,
    }

    private State _state = State.Closed;

    private PreviewTrigger _trigger = PreviewTrigger.Hover;

    /// <summary>The card the panel is showing (or, while pending, is dwelling toward).</summary>
    public long? Card { get; private set; }

    /// <summary>The last keyboard selection seen, followed once it settles.</summary>
    private long? _keyboardTarget;

    private long _keyboardTargetAt;

    /// <summary>When the pointer entered the card it is dwelling on.</summary>
    private long _hoverEnteredAt;

    /// <summary>When the pointer left the last card, starting the grace buffer.</summary>
    private long _bufferLeftAt;

    public PreviewPolicy(int hoverDelayMs, Func<long> clock)
    {
        _hoverDelayMs = hoverDelayMs;
        Clock = clock;
    }

    /// <summary>
    /// The policy a settings snapshot asks for. With 悬停自动预览 off the dwell
    /// is zero — hovering opens nothing, and Space still opens the panel,
    /// owned by the key. With it on, the dwell is at least a millisecond, so
    /// a zero that slipped past loading can never quietly mean "off".
    /// </summary>
    public static PreviewPolicy For(AppSettings settings, Func<long> clock)
        => new(settings.PreviewOnHover ? Math.Max(1, settings.PreviewHoverDelayMs) : 0, clock);

    /// <summary>Monotonic milliseconds; injected so tests can move time.</summary>
    public Func<long> Clock { get; }

    public bool IsOpen => _state is State.Open or State.Buffering or State.OverPreview;

    public bool HoverEnabled => _hoverDelayMs > 0;

    // --- events ---------------------------------------------------------------

    /// <summary>The pointer entered a card.</summary>
    public PreviewCommand HoverEnter(long card)
    {
        switch (_state)
        {
            case State.Buffering when Card == card:
                // Straight back: the pointer came home mid-buffer.
                _state = State.Open;
                return PreviewCommand.None;

            case State.Buffering:
                // A neighbouring card reached inside the grace period: the
                // panel glides across instead of dying and being reborn.
                _state = State.Open;
                Card = card;
                _trigger = PreviewTrigger.Hover;
                return PreviewCommand.Retarget;

            case State.Closed when HoverEnabled:
                _state = State.PendingHover;
                Card = card;
                _trigger = PreviewTrigger.Hover;
                _hoverEnteredAt = Clock();
                return PreviewCommand.None;

            case State.OverPreview when Card == card:
                // Off the panel back onto its own card: nothing to carry over.
                _state = State.Open;
                _trigger = PreviewTrigger.Hover;
                return PreviewCommand.None;

            case State.OverPreview:
                // Off the panel onto a card: same crossing, same glide.
                _state = State.Open;
                Card = card;
                _trigger = PreviewTrigger.Hover;
                return PreviewCommand.Retarget;

            case State.Open when _trigger == PreviewTrigger.Keyboard && Card == card:
                // The pointer arrived on the held card: ownership passes to
                // the hover rules, so releasing the key leaves it standing
                // rather than snapping it shut under a resting pointer. Only
                // this card — a held preview does not become hover-owned
                // merely because the pointer swept across the neighbours.
                _trigger = PreviewTrigger.Hover;
                return PreviewCommand.None;

            default:
                return PreviewCommand.None;
        }
    }

    /// <summary>The pointer left a card. A hover preview starts its buffer; a held one stays.</summary>
    public PreviewCommand HoverLeave()
    {
        switch (_state)
        {
            case State.Open when _trigger == PreviewTrigger.Hover:
                _state = State.Buffering;
                _bufferLeftAt = Clock();
                return PreviewCommand.None;

            case State.PendingHover:
                _state = State.Closed;
                Card = null;
                return PreviewCommand.None;

            default:
                return PreviewCommand.None;
        }
    }

    /// <summary>The pointer moved onto the preview panel itself: the buffer stands down while it rests there.</summary>
    public PreviewCommand PreviewEntered()
    {
        if (_state == State.Buffering)
        {
            _state = State.OverPreview;
        }

        return PreviewCommand.None;
    }

    /// <summary>The pointer left the preview panel: the buffer starts over.</summary>
    public PreviewCommand PreviewLeft()
    {
        if (_state == State.OverPreview)
        {
            _state = State.Buffering;
            _bufferLeftAt = Clock();
        }

        return PreviewCommand.None;
    }

    /// <summary>
    /// Space went down: open now, owned by the key until it comes up. A held
    /// key repeats its key-down every ~30 ms, and a hover preview may already
    /// show the card: when the panel is up on this very card, the key only
    /// takes ownership — reopening it each time reset the picture to its
    /// thumbnail and threw away the original in flight, and the preview
    /// flickered between blurry and sharp (用户实录 2026-10-09).
    /// </summary>
    public PreviewCommand SpaceDown(long card)
    {
        var showing = IsOpen && Card == card;

        _state = State.Open;
        Card = card;
        _trigger = PreviewTrigger.Keyboard;
        return showing ? PreviewCommand.None : PreviewCommand.Open;
    }

    /// <summary>Space came up: close only what the key opened.</summary>
    public PreviewCommand SpaceUp()
    {
        if (_state != State.Closed && _trigger == PreviewTrigger.Keyboard)
        {
            _state = State.Closed;
            Card = null;
            return PreviewCommand.Close;
        }

        return PreviewCommand.None;
    }

    /// <summary>The keyboard selection moved: noted, followed only once it settles.</summary>
    public PreviewCommand SelectionMoved(long card)
    {
        _keyboardTarget = card;
        _keyboardTargetAt = Clock();
        return PreviewCommand.None;
    }

    /// <summary>The list was scrolled. Hover previews end; held ones answer to the key alone.</summary>
    public PreviewCommand Scrolled()
    {
        if (_state is State.Open or State.Buffering or State.OverPreview
            && _trigger == PreviewTrigger.Hover)
        {
            _state = State.Closed;
            Card = null;
            return PreviewCommand.Close;
        }

        if (_state == State.PendingHover)
        {
            _state = State.Closed;
            Card = null;
        }

        return PreviewCommand.None;
    }

    /// <summary>Escape: whatever is up comes down.</summary>
    public PreviewCommand Escape()
    {
        if (_state != State.Closed)
        {
            _state = State.Closed;
            Card = null;
            return PreviewCommand.Close;
        }

        return PreviewCommand.None;
    }

    /// <summary>The bar hid; a preview cannot stay anchored to an invisible list.</summary>
    public PreviewCommand BarHidden()
    {
        _keyboardTarget = null;
        if (_state != State.Closed)
        {
            _state = State.Closed;
            Card = null;
            return PreviewCommand.Close;
        }

        return PreviewCommand.None;
    }

    /// <summary>
    /// The heartbeat, driven by one repeating timer while the bar is visible.
    /// Fires the dwell-open and the buffer expiry, and follows a settled
    /// keyboard selection.
    /// </summary>
    public PreviewCommand Tick()
    {
        var now = Clock();

        if (_state == State.PendingHover && now - _hoverEnteredAt >= _hoverDelayMs)
        {
            _state = State.Open;
            _trigger = PreviewTrigger.Hover;
            return PreviewCommand.Open;
        }

        if (_state == State.Buffering && now - _bufferLeftAt >= HoverBufferMs)
        {
            _state = State.Closed;
            Card = null;
            return PreviewCommand.Close;
        }

        if (_state == State.Open
            && _trigger == PreviewTrigger.Keyboard
            && _keyboardTarget is { } target
            && target != Card
            && now - _keyboardTargetAt >= KeyboardSettleMs)
        {
            Card = target;
            return PreviewCommand.Retarget;
        }

        return PreviewCommand.None;
    }

    /// <summary>
    /// How much longer the next time-based decision is waiting for, or null
    /// when nothing is pending and no timer need run (O-37).
    ///
    /// The window arms one single-shot timer for exactly this long after
    /// every event; a bar that is merely visible — no hover dwelling, no
    /// buffer running, no key held — asks for no wake-ups at all, where the
    /// old fixed 50 ms tick spun for the whole time the bar was on screen.
    /// The deadline is the same instant <see cref="Tick"/> would have acted
    /// on, so arming and firing land identically to polling did.
    /// </summary>
    public TimeSpan? TimeUntilDecision()
    {
        var now = Clock();

        var wait = _state switch
        {
            State.PendingHover => _hoverDelayMs - (now - _hoverEnteredAt),
            State.Buffering => HoverBufferMs - (now - _bufferLeftAt),
            State.Open when _trigger == PreviewTrigger.Keyboard
                && _keyboardTarget is { } target
                && target != Card
                => KeyboardSettleMs - (now - _keyboardTargetAt),
            _ => (long?)null,
        };

        // A deadline already reached reports zero: the caller fires the
        // decision now rather than sleeping a negative span.
        return wait is null ? null : TimeSpan.FromMilliseconds(Math.Max(0, wait.Value));
    }
}
