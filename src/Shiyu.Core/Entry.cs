namespace Shiyu.Core;

public enum EntryKind
{
    Text,
    Image,
    Files,
}

/// <summary>
/// One record in the clipboard history. Every copy produces an entry —
/// filtering decides whether the badge appears, never whether we record.
/// </summary>
/// <param name="Text">For an image entry, a short human-readable stand-in.</param>
/// <param name="ThumbnailPng">
/// Kept forever, unlike the original. That is what stops the history developing
/// holes the user cannot explain once old images are cleaned up.
/// </param>
/// <param name="OriginalPath">
/// Where the full-size image lives, until retention removes it. Null once it is
/// gone — the entry and its thumbnail stay.
/// </param>
public sealed record Entry(
    long Id,
    string Text,
    string? SourceApp,
    DateTimeOffset CreatedAt)
{
    public EntryKind Kind { get; init; } = EntryKind.Text;

    /// <summary>What a text entry is — a link, an email, a colour, a path — so the list can show it as itself.</summary>
    public EntrySubtype Subtype { get; init; } = EntrySubtype.None;

    /// <summary>
    /// The copy's HTML form, kept for pasting back with formatting. The Text stays the searchable plain form.
    /// </summary>
    /// <remarks>Side-table payload: null on entries returned by list queries — see <see cref="ThumbnailPng"/>.</remarks>
    public string? Html { get; init; }

    /// <summary>
    /// The copy's RTF form, when the source published one instead of HTML.
    /// </summary>
    /// <remarks>Side-table payload: null on entries returned by list queries — see <see cref="ThumbnailPng"/>.</remarks>
    public string? Rtf { get; init; }

    /// <summary>The paths of a file copy, capped at <see cref="FileEntries.Cap"/>. Empty for other kinds.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The file paths that still exist, checked lazily for display.</summary>
    public IReadOnlyList<string> AliveFiles => Files.Where(FileEntries.Alive).ToList();

    /// <summary>
    /// The image's thumbnail PNG — see the record's param doc for what it is
    /// for. A payload column in the entry_blobs side table: list queries
    /// (Recent, Page, Find, Search, MostRecent) never read it, so an entry
    /// they return carries null. <see cref="EntryStore.Get(long)"/> attaches
    /// it, <see cref="EntryStore.BlobsOf"/> fetches it for a page of cards,
    /// and <see cref="EntryStore.EntriesWithBlobsAfter(long, int)"/> walks the
    /// whole library with payloads for export.
    /// </summary>
    public byte[]? ThumbnailPng { get; init; }

    public string? OriginalPath { get; init; }

    /// <summary>
    /// The original image's pixel size, recorded when it was copied so the
    /// preview panel can know its shape without loading anything. Zero for
    /// rows that predate the column — and for every non-image.
    /// </summary>
    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    /// <summary>Pinned entries sort ahead of everything else.</summary>
    public bool IsPinned { get; init; }

    /// <summary>
    /// Belongs to the favourites collection. Never moves the entry — that is
    /// the pin's job; the top of the list is for what is in use right now.
    /// </summary>
    public bool Favorite { get; init; }

    /// <summary>
    /// The user's own words for this entry. When present it is the entry's
    /// public face; the original content waits behind a hover.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>How many times this entry was copied or pasted back out.</summary>
    public int UseCount { get; init; }

    /// <summary>
    /// The pile this entry was filed into, or null for ungrouped. Unlike tags
    /// this is exclusive — an entry is in one group or in none.
    /// </summary>
    public long? GroupId { get; init; }

    /// <summary>
    /// The entry this translation was made from, when the entry is one. The
    /// link is a reference, not a leash: the original going away leaves the
    /// translation standing, only unlinked.
    /// </summary>
    public long? TranslatedFrom { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// 图里认出来的字（评审 2026-10-16 F-07）：null 是还没认过，空串是认过、没有字。只图片条目有；进全文索引，
    /// 搜截图里的词也搜得到。
    /// </summary>
    public string? OcrText { get; init; }

    /// <summary>False once retention has removed the original from disk.</summary>
    public bool HasOriginal => OriginalPath is { Length: > 0 } path && File.Exists(path);
}

/// <summary>An entry not yet written down, used for bulk writes.</summary>
public sealed record NewEntry(string Text, string? SourceApp, DateTimeOffset CreatedAt);

/// <summary>
/// One entry's payload row from the entry_blobs side table: the columns that
/// left the entries table so lists could read a narrow row. Fetched by
/// <see cref="EntryStore.BlobsOf"/> for the cards a page is about to show.
/// </summary>
public sealed record EntryBlobs(long EntryId, byte[]? ThumbnailPng, string? Html, string? Rtf);
