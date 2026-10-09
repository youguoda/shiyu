using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// One card's view state. A class rather than a record because selection is
/// mutable and the card's own highlight follows it.
/// </summary>
internal sealed class BarCard : INotifyPropertyChanged
{
    public long Id { get; init; }

    public EntryKind Kind { get; init; }

    /// <summary>The full text, kept for copying; the card shows the clamped preview.</summary>
    public required string Text { get; init; }

    public string Preview { get; init; } = string.Empty;

    /// <summary>
    /// What the preview panel's teach row shows for this card: key caps with
    /// short actions, the drag step naming this kind's own destination
    /// (<see cref="PreviewTeaching"/>).
    /// </summary>
    public IReadOnlyList<TeachStep> Teaching { get; init; } = [];

    /// <summary>
    /// The card-hint setting (bar.card-tooltips): controls the preview panel's
    /// teach row. The card itself carries no system tooltip any more — closing
    /// one mid-show twice left blank shells over the list (2026-10-05).
    /// </summary>
    public bool ShowToolTip { get; init; } = true;

    public string KindText { get; init; } = string.Empty;

    /// <summary>
    /// 纯文本卡不显示类型标签（§6.1/U-16）：左上扫读位只留给带信息量的
    /// 标签——"链接""图片 · 1920×1080""3 个文件"。空文本即整组隐藏。
    /// </summary>
    public Visibility KindLabelVisibility => string.IsNullOrEmpty(KindText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    /// <summary>
    /// The facet glyph beside the kind word (票 39): the same IconGlyph family
    /// the header's type chips use, so a facet and its actions never draw two
    /// symbols for one idea.
    /// </summary>
    public string KindGlyph { get; init; } = string.Empty;

    /// <summary>How often this entry has been used; shown on hover only (票 39).</summary>
    public int UseCount { get; init; }

    /// <summary>
    /// The type badge's tooltip: the word, plus the usage count the badge
    /// itself no longer carries — the number is a reward, not an identity.
    /// </summary>
    public string KindToolTip => UseCount > 0
        ? $"{KindText}{Environment.NewLine}用过 {UseCount} 次"
        : KindText;

    public string WhenText { get; init; } = string.Empty;

    /// <summary>The absolute stamp (and usage count) behind the relative one.</summary>
    public string WhenToolTip { get; init; } = string.Empty;

    /// <summary>
    /// The number key that pastes this row, when it is one of the first ten
    /// displayed rows; null otherwise. Assigned from display position.
    /// </summary>
    public string? RowKeyText { get; set; }

    public ImageSource? Icon { get; init; }

    private ImageSource? _thumbnail;

    /// <summary>
    /// The card's thumbnail. Arrives late by design (O-36): the row is built
    /// with the placeholder colour block the template already paints, and the
    /// decode lands here from a background thread when it is ready — the
    /// property mutates in place so the row does not have to be rebuilt
    /// around it.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            Changed(nameof(Thumbnail));
        }
    }

    public string? OriginalPath { get; init; }

    public bool HasOriginal { get; init; }

    public EntrySubtype Subtype { get; init; }

    /// <summary>
    /// The entry's formatted forms are deliberately not carried here: rows are
    /// narrow by design (O-22), so an action that pastes or drags with
    /// formatting fetches them from the store at the moment it acts — one
    /// primary-key read for something the user does once, instead of a payload
    /// column on every listed card.
    /// </summary>

    /// <summary>The file rows a file card shows, already clamped to the density knob.</summary>
    public IReadOnlyList<FileRow> FileRows { get; init; } = [];

    private ImageSource? _filePreviewSource;

    /// <summary>
    /// A file copy made entirely of images previews its first file. Like
    /// <see cref="Thumbnail"/>, it backfills from a background decode (O-36):
    /// the border the image sits in is a colour block until then.
    /// </summary>
    public ImageSource? FilePreviewSource
    {
        get => _filePreviewSource;
        set
        {
            _filePreviewSource = value;
            Changed(nameof(FilePreviewSource));
            Changed(nameof(FilePreviewVisibility));
        }
    }

    /// <summary>True when this entry is Shiyu's own kept translation of another.</summary>
    public bool IsTranslation { get; init; }

    public Visibility TranslationVisibility => IsTranslation
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility FilePreviewVisibility => FilePreviewSource is null
        ? Visibility.Collapsed
        : Visibility.Visible;

    /// <summary>The full capped path list of a file entry, for copying and pasting back.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    private bool _favorite;

    /// <summary>Mutates in place: favouriting must not disturb the list around it.</summary>
    public bool Favorite
    {
        get => _favorite;
        set
        {
            _favorite = value;
            Changed(nameof(Favorite));
            // The watermark binds the visibility, not the flag — without this
            // the star never appears, and un-favouriting looks impossible.
            Changed(nameof(FavoriteVisibility));
        }
    }

    public Visibility FavoriteVisibility => Favorite ? Visibility.Visible : Visibility.Collapsed;

    private string? _groupBadge;

    /// <summary>
    /// The name of the pile this entry was filed into. Mutates in place, like
    /// the favourite flag: filing is organisation, not reordering.
    /// </summary>
    public string? GroupBadge
    {
        get => _groupBadge;
        set
        {
            _groupBadge = value;
            Changed(nameof(GroupBadge));
            Changed(nameof(GroupBadgeVisibility));
        }
    }

    public Visibility GroupBadgeVisibility => string.IsNullOrEmpty(GroupBadge)
        ? Visibility.Collapsed
        : Visibility.Visible;

    private string? _note;

    /// <summary>Mutates in place: the note becomes the entry's public face the moment it is saved.</summary>
    public string? Note
    {
        get => _note;
        set
        {
            _note = value;
            Changed(nameof(Note));
        }
    }

    private string _face = string.Empty;

    /// <summary>What the body shows: the note by default, the original on hover.</summary>
    public string Face
    {
        get => _face;        set
        {
            _face = value;
            Changed(nameof(Face));
        }
    }

    private bool _allPathsDead;

    /// <summary>
    /// True when every path of a file entry is gone — the card shows it struck
    /// through and faded. Rendered from the existence cache's verdicts (O-36)
    /// and corrected in place when a background probe lands: the first paint
    /// trusts the cache, because the cache never waits on the disk.
    /// </summary>
    public bool AllPathsDead
    {
        get => _allPathsDead;
        set
        {
            _allPathsDead = value;
            Changed(nameof(AllPathsDead));
        }
    }

    public int FileCount { get; init; }

    public Visibility FilesVisibility =>
        Kind == EntryKind.Files ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The quiet count line under the file rows.</summary>
    public string FileTail => FileCount switch
    {
        0 => string.Empty,
        1 => "1 个项目",
        _ => $"共 {FileCount} 项",
    };

    /// <summary>Links and emails open in the system's default program.</summary>
    public bool IsOpenable => Subtype is EntrySubtype.Link or EntrySubtype.Email;

    /// <summary>The parsed colour of a colour entry, as a frozen brush ready to paint.</summary>
    public Brush? SwatchBrush { get; init; }

    public Visibility SwatchVisibility =>
        SwatchBrush is null ? Visibility.Collapsed : Visibility.Visible;

    public int TextLines { get; init; }

    /// <summary>
    /// The text clamp as a height of whole Content lines — the card body is
    /// Type.Content (票 32/39), so the clamp counts its lines: 31 DIP at the
    /// standard 内容字号, whatever the setting says otherwise (2026-10-09; a
    /// change rebuilds the cards, see BarWindow.ApplySettings).
    /// Visually identical to MaxLines, usable from XAML on this build (see the
    /// template comment).
    /// </summary>
    public double TextMaxHeight
        => TextLines * ThemeManager.Content.ContentLine;

    public int ImageHeight { get; init; }

    /// <summary>The original image's pixel size, for the preview panel's pre-computed shape.</summary>
    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public bool IsPinned { get; init; }

    /// <summary>
    /// True while delete protection covers this card (票 39's 🔐 corner slot):
    /// the lock watermark binds the visibility, not the flag.
    /// </summary>
    public bool IsProtected { get; init; }

    public Visibility ProtectionVisibility => IsProtected
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FallbackIconVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImageVisibility =>
        Kind == EntryKind.Image ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PinnedVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            Changed(nameof(IsSelected));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One row of a file card: a name, its type icon, and whether the path still
/// exists. A class with a notifying <see cref="Dead"/> flag because the flag
/// is rendered from the existence cache and corrected when the background
/// probe answers (O-36).
/// </summary>
internal sealed class FileRow : INotifyPropertyChanged
{
    public FileRow(string name, string fullPath, ImageSource? icon, bool dead)
    {
        Name = name;
        FullPath = fullPath;
        Icon = icon;
        _dead = dead;
    }

    public string Name { get; }

    public string FullPath { get; }

    public ImageSource? Icon { get; }

    private bool _dead;

    public bool Dead
    {
        get => _dead;
        set
        {
            _dead = value;
            Changed(nameof(Dead));
            Changed(nameof(DeadVisibility));
        }
    }

    public Visibility DeadVisibility => Dead ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
