namespace Shiyu.Core;

/// <summary>
/// File-entry rules: the cap on how many paths one entry keeps, and the label
/// that has to tell the truth about that cap. A user who copies a 500-file
/// selection should see "500 项" in the list, not a lie by omission.
/// </summary>
public static class FileEntries
{
    /// <summary>
    /// One entry keeps at most this many paths. Explorer happily lets a user
    /// select thousands; storing them all buys nothing a paste needs — the
    /// cap keeps the database and the list bounded.
    /// </summary>
    public const int Cap = 64;

    /// <summary>
    /// The paths worth keeping, and whether the cap left anything behind.
    /// </summary>
    public static IReadOnlyList<string> WithinCap(IReadOnlyList<string> paths, out bool capped)
    {
        capped = paths.Count > Cap;
        return capped ? paths.Take(Cap).ToList() : paths;
    }

    /// <summary>
    /// The entry's human label: the first file's name, a count when there are
    /// several, and the honest total when the cap bit.
    /// </summary>
    public static string Label(IReadOnlyList<string> paths, bool capped)
    {
        if (paths.Count == 0)
        {
            return "空文件列表";
        }

        var first = Path.GetFileName(paths[0]);
        first = first.Length == 0 ? paths[0] : first;

        if (paths.Count == 1)
        {
            return first;
        }

        return capped
            ? $"{first} 等 {paths.Count}+ 项"
            : $"{first} 等 {paths.Count} 项";
    }

    private static readonly HashSet<string> PreviewExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico",
        };

    /// <summary>
    /// The first path of a copy that is entirely image files, or null.
    ///
    /// All-or-nothing on purpose: a preview of the first file next to rows for
    /// the others would imply the picture stands for the whole selection only
    /// sometimes, and the card would never be able to say which.
    /// </summary>
    public static string? PreviewImagePath(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Any(path => !PreviewExtensions.Contains(Path.GetExtension(path))))
        {
            return null;
        }

        return paths[0];
    }

    /// <summary>
    /// 文件与目录都算「在」：复制对象可以是文件夹，打开与定位对目录一样有效。
    /// 裸 <c>File.Exists</c> 对目录恒为 false——文件夹的打开/定位因此一直没反应
    /// （用户实录 2026-10-19）。
    /// </summary>
    public static bool Alive(string path)
        => File.Exists(path) || Directory.Exists(path);

    /// <summary>列表里第一个还「在」的路径；都不在了为 null。</summary>
    public static string? FirstAlive(IReadOnlyList<string> paths)
        => paths.FirstOrDefault(Alive);
}
