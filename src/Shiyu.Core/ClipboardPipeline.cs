using System.Text;

namespace Shiyu.Core;

/// <summary>
/// Turns clipboard changes into entries. Recording is greedy: every copy is
/// kept, regardless of what any later filter decides about the badge. The one
/// exception is exclusion, which is not a filter at all — excluded content is
/// never written down in the first place.
/// </summary>
public sealed partial class ClipboardPipeline : IDisposable
{
    private readonly IClipboardMonitor _clipboard;
    private readonly EntryStore _store;
    private readonly TimeProvider _clock;
    private ExclusionPolicy _exclusions;
    private readonly ImageArchive? _images;
    private readonly SourceIconCache? _icons;

    /// <summary>
    /// Raised after an entry is recorded, when its content is the kind a user
    /// might want translated. Raised from here rather than from the window so
    /// the ordering that matters — excluded content never reaches the badge —
    /// is settled in one place and can be tested.
    /// </summary>
    public event Action<string>? BadgeDeserved;

    /// <summary>
    /// Raised when an image could not be saved. The user is told rather than
    /// left with a copy that silently never appeared.
    /// </summary>
    public event Action<string>? ImageFailed;

    /// <summary>Completes when any in-flight image work has finished.</summary>
    public Task Idle { get; private set; } = Task.CompletedTask;

    public ClipboardPipeline(
        IClipboardMonitor clipboard,
        EntryStore store,
        TimeProvider clock,
        ExclusionPolicy exclusions,
        ImageArchive? images = null,
        SourceIconCache? icons = null)
    {
        _clipboard = clipboard;
        _store = store;
        _clock = clock;
        _exclusions = exclusions;
        _images = images;
        _icons = icons;
        _clipboard.Changed += OnClipboardChanged;
    }

    /// <summary>
    /// Swaps in a new set of rules. Takes effect from the very next copy, so a
    /// user who adds a rule does not have to restart before it protects them.
    /// </summary>
    public void UseExclusions(ExclusionPolicy exclusions) => _exclusions = exclusions;

    /// <summary>
    /// Whether copied file lists are recorded. Off means a file copy leaves
    /// no entry — the choice is "not recorded", never "recorded and hidden".
    /// </summary>
    public bool RecordFiles { get; set; } = true;

    /// <summary>Whether copied images are recorded. Text is always recorded.</summary>
    public bool RecordImages { get; set; } = true;

    /// <summary>
    /// Whether a copy that deserves a badge is offered one (「翻译徽标」, user
    /// request 2026-10-13). Off changes nothing about recording: the entry is
    /// written either way, only <see cref="BadgeDeserved"/> stays quiet.
    /// </summary>
    public bool OfferBadges { get; set; } = true;

    /// <summary>
    /// 暂停记录（评审 2026-10-16 F-06）：暂停期间到的复制既不落库、也不弹徽标。按复制那一刻的时间判定，
    /// 到点即恢复，不等任何定时器。
    /// </summary>
    public RecordingPause? Pause { get; set; }

    /// <summary>
    /// The fingerprint of the last recorded image, with the clipboard arrival
    /// it was taken at. The same copy arrives more than once — an application
    /// publishes its bitmap in several clipboard formats in turn, and each
    /// publication raises its own notification — and collapsing the repeat
    /// mirrors what the text path does.
    ///
    /// This lives here rather than in the monitor because the fingerprint
    /// only exists after the decode, and the decode happens off the message
    /// thread (O-36). That asynchrony is why the reset is expressed through
    /// arrival numbers rather than nulling a field: the text that supersedes
    /// an image may record while that image's decode is still running, and a
    /// late write must not resurrect an image the user already moved past.
    /// </summary>
    private readonly object _imageStateGuard = new();

    private (long Arrival, long Fingerprint) _lastImage;

    /// <summary>The arrival every non-image copy last superseded.</summary>
    private long _lastNonImageArrival;

    /// <summary>Every snapshot's arrival number, in clipboard order.</summary>
    private long _arrivals;

    private void OnClipboardChanged(ClipboardSnapshot snapshot)
    {
        // 暂停记录：照收通知，不留痕迹（评审 2026-10-16 F-06）。
        if (Pause?.IsPaused(_clock.GetUtcNow()) == true)
        {
            return;
        }

        // Checked first, and before anything is written or even read back:
        // excluded content must leave no trace at all, not a trace that is
        // later filtered out of view.
        if (_exclusions.Excludes(snapshot))
        {
            return;
        }

        var arrival = System.Threading.Interlocked.Increment(ref _arrivals);

        // The copy is now definitely going to be an entry, so this is the
        // moment to make sure its source application has a cached icon — the
        // executable is at hand and alive. Ensure swallows its own failures;
        // recording never depends on it.
        _icons?.Ensure(snapshot.SourceApp, snapshot.SourceExePath);

        // A file copy: one entry for any number of files, like text and image
        // before it. Identical repeats of the newest file list touch it rather
        // than duplicating it.
        if (snapshot.Files is { Count: > 0 } files)
        {
            if (!RecordFiles)
            {
                return;
            }

            // Files supersede whatever image came before them, so the next
            // image is judged fresh rather than against something long gone.
            SupersedeImage(arrival);

            var newest = _store.MostRecent();
            if (newest is { Kind: EntryKind.Files } && newest.Files.SequenceEqual(files))
            {
                _store.Touch(newest.Id, _clock.GetUtcNow());
                return;
            }

            _store.AppendFiles(files, snapshot.SourceApp, _clock.GetUtcNow());
            Log.Event(LogEvent.ClipboardRecorded, ("kind", (int)EntryKind.Files), ("count", files.Count));
            return;
        }

        if (snapshot.Image is { } image && _images is not null)
        {
            if (!RecordImages)
            {
                return;
            }

            // Chained rather than fired and forgotten, so two quick copies are
            // recorded in the order they happened rather than whichever
            // encodes faster.
            Idle = Idle.ContinueWith(
                _ => RecordImage(image, snapshot.SourceApp, arrival),
                TaskScheduler.Default).Unwrap();
            return;
        }

        RecordText(snapshot, arrival);
    }

    /// <summary>
    /// Notes that a non-image copy arrived: later images are judged against
    /// nothing, no matter when the previous image's decode finishes.
    /// </summary>
    private void SupersedeImage(long arrival)
        => System.Threading.Volatile.Write(ref _lastNonImageArrival, arrival);

    private void RecordText(ClipboardSnapshot snapshot, long arrival)
    {
        var now = _clock.GetUtcNow();

        // Text supersedes whatever image came before it, so the next image is
        // judged fresh rather than against something long gone.
        SupersedeImage(arrival);

        // A single user copy can raise more than one clipboard notification,
        // because applications publish several formats in turn. Collapsing a
        // repeat of the newest entry keeps those phantom duplicates out of the
        // history; a repeat that is not adjacent still earns its own entry, so
        // the chronology stays honest.
        var newest = _store.MostRecent();
        if (newest is not null && newest.Kind == EntryKind.Text && newest.Text == snapshot.Text)
        {
            _store.Touch(newest.Id, now);
            Offer(snapshot.Text);
            return;
        }

        _store.Append(
            snapshot.Text,
            snapshot.SourceApp,
            now,
            WithinLimit(snapshot.Html),
            WithinLimit(snapshot.Rtf));
        Log.Event(LogEvent.ClipboardRecorded, ("kind", (int)EntryKind.Text));
        Offer(snapshot.Text);
    }

    private async Task RecordImage(IClipboardImage image, string? sourceApp, long arrival)
    {
        try
        {
            var rendered = await image.RenderAsync();

            // The same copy arriving twice — several formats published in
            // turn, one notification each — collapses here, on the same
            // thread the decode already paid for. Chaining (Idle) keeps the
            // collapse honest: two quick copies judge in the order they
            // happened. The previous image only counts while nothing else
            // arrived after it: text or files between the two copies mean
            // the repeat is a fresh decision, not a phantom republication.
            lock (_imageStateGuard)
            {
                if (rendered.Fingerprint != 0
                    && _lastImage is { } previous
                    && previous.Arrival > System.Threading.Volatile.Read(ref _lastNonImageArrival)
                    && previous.Fingerprint == rendered.Fingerprint)
                {
                    return;
                }

                _lastImage = (arrival, rendered.Fingerprint);
            }

            var now = _clock.GetUtcNow();
            var path = _images!.Save(rendered.FullPng, now);

            _store.AppendImage(
                $"图片 {rendered.Width}×{rendered.Height}",
                rendered.ThumbnailPng,
                path,
                sourceApp,
                now,
                rendered.Width,
                rendered.Height);
            Log.Event(
                LogEvent.ClipboardRecorded,
                ("kind", (int)EntryKind.Image), ("width", rendered.Width), ("height", rendered.Height));
        }
        catch (Exception failure)
        {
            // A failed image must not take the application down, and must not
            // vanish without a word either — the user saw themselves copy it.
            Log.Event(LogEvent.ClipboardImageFailed, failure);
            ImageFailed?.Invoke(failure.Message);
        }
    }

    /// <summary>
    /// The ceiling on a stored formatted form. A whole-page copy can be
    /// megabytes; keeping it would bloat the database for a formatting nobody
    /// will paste back, so past that the plain text stands alone.
    /// </summary>
    private const int FormattedLimitBytes = 512 * 1024;

    private static string? WithinLimit(string? formatted)
        => formatted is { Length: > 0 } && Encoding.UTF8.GetByteCount(formatted) <= FormattedLimitBytes
            ? formatted
            : null;

    private void Offer(string text)
    {
        if (OfferBadges && ContentClassifier.DeservesBadge(text))
        {
            BadgeDeserved?.Invoke(text);
        }
    }

    public void Dispose() => _clipboard.Changed -= OnClipboardChanged;
}
