namespace Shiyu.Core;

/// <summary>
/// The quick actions a hovered card can offer. Identified by stable ids
/// because the user's chosen set and order live in the settings file.
/// </summary>
public static class HoverActions
{
    /// <summary>Every action id that exists, in default order.</summary>
    public static readonly string[] All =
        ["copy", "paste", "plain", "ocr", "open", "locate", "pin", "favorite", "note", "group", "delete"];

    /// <summary>「复制图中文字」加进来之前（评审 2026-10-16 F-07）的默认清单：原样没动过的，升级时换成新的默认。</summary>
    public static readonly string[] FormerDefaultBeforeOcr =
        ["copy", "paste", "plain", "open", "locate", "pin", "favorite", "note", "group", "delete"];

    /// <summary>The ids the previous default shipped with, before groups.</summary>
    public static readonly string[] FormerDefaultWithoutGroup =
        ["copy", "paste", "plain", "open", "locate", "pin", "favorite", "note", "delete"];

    /// <summary>The id as the interface shows it.</summary>
    public static string Name(string id) => id switch
    {
        "copy" => "复制",
        "paste" => "粘贴",
        "plain" => "纯文本粘贴",
        "ocr" => "复制图中文字",
        "open" => "打开",
        "locate" => "定位",
        "pin" => "置顶",
        "favorite" => "收藏",
        "note" => "备注",
        "group" => "归组",
        "delete" => "删除",
        _ => id,
    };

    /// <summary>The glyph the tray button carries.</summary>
    public static string Glyph(string id) => id switch
    {
        "copy" => "复",
        "paste" => "贴",
        "plain" => "文",
        "ocr" => "字",
        "open" => "开",
        "locate" => "位",
        "pin" => "钉",
        "favorite" => "★",
        "note" => "注",
        "group" => "组",
        "delete" => "删",
        _ => "?",
    };

    /// <summary>
    /// The system symbol glyph for the tray button, from Segoe Fluent Icons
    /// (the codepoints are shared with Segoe MDL2 Assets on Windows 10). The
    /// single-character Chinese mark stays in the tooltip and the key-badge
    /// system as the app's brand — the symbol carries recognition, the mark
    /// carries identity.
    /// </summary>
    public static string IconGlyph(string id) => id switch
    {
        "copy" => "\uE8C8",   // Copy
        "paste" => "\uE77F",  // Paste
        "plain" => "\uE8D2",  // Font — text without formatting
        "ocr" => "\uE7C3",    // Page — 图里认出来的字（评审 2026-10-16 F-07）
        "open" => "\uE8E5",   // OpenFile
        "locate" => "\uE702", // MapPin
        "pin" => "\uE718",    // Pin
        "favorite" => "\uE735", // FavoriteStarFill
        "note" => "\uE70B",   // QuickNote
        "group" => "\uE8B7",  // Folder — a pile to file into
        "delete" => "\uE74D", // Delete
        _ => Glyph(id),
    };

    /// <summary>Deleting cannot be undone, so it never looks like its neighbours.</summary>
    public static bool IsDestructive(string id) => id == "delete";

    /// <summary>
    /// Cleans a hand-edited choice: unknown ids dropped, duplicates dropped,
    /// the user's order kept. An unusable result falls back to the default
    /// rather than to an empty tray, because a tray with nothing in it reads
    /// as a broken feature.
    /// </summary>
    public static IReadOnlyList<string> Sanitise(IEnumerable<string>? chosen)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();

        foreach (var id in chosen ?? [])
        {
            if (All.Contains(id) && seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result.Count > 0 ? result : All;
    }

    /// <summary>
    /// Which of the chosen actions this entry can honour. The rest are hidden:
    /// a button that shows up only to report that it cannot work has already
    /// wasted the user's click.
    /// </summary>
    /// <param name="deleteProtected">
    /// True when deletion is off the table for this entry — a favourite or a
    /// pin under protection. The delete action then vanishes rather than
    /// greying out: a greyed delete is a button users keep poking.
    /// </param>
    public static IReadOnlyList<string> AvailableFor(
        IEnumerable<string> chosen, EntryKind kind, bool hasOriginal, bool deleteProtected = false)
        => chosen.Where(id => id switch
        {
            // Stored text is plain; pasting "as plain text" means something
            // only where formatted content could have been.
            "plain" => kind == EntryKind.Text,

            // 图里的字（评审 2026-10-16 F-07）：只有图片有；认过的用存下的，没认过的当场认。
            "ocr" => kind == EntryKind.Image,

            // Files carry their own paths to act on; images need the retained
            // original.
            "open" or "locate" => hasOriginal || kind == EntryKind.Files,

            "delete" => !deleteProtected,
            _ => true,
        }).ToList();
}
