namespace Shiyu.Core;

/// <summary>
/// 回贴前对剪贴板的快照（票 43）：只存拾语的 <c>WindowsClipboardWriter</c> 能写回去的格式——
/// 文本、HTML、RTF、文件列表——外加"为什么不能还原"的证据。只在内存里短暂存在，不进历史。
/// </summary>
/// <param name="Files">文件列表（CF_HDROP）；没有就是空表。</param>
/// <param name="Excluded">
/// 带排除标记（<c>ExcludeClipboardContentFromMonitorProcessing</c> 或值为 0 的
/// <c>CanIncludeInClipboardHistory</c>）。带标记的快照<b>不读内容</b>：密码管理器复制出来的东西，
/// 连在内存里多留一份都没有必要。
/// </param>
/// <param name="LostFormats">
/// 剪贴板上有、但写不回去的、携带内容的格式名（图片、位图、图元文件、虚拟文件……）。
/// 有一个就不还原：那样剪贴板上留下的只是残缺的一份。
/// </param>
/// <param name="Empty">剪贴板上什么格式都没有——还原就是清空。</param>
public sealed record ClipboardBackup(
    string? Text,
    string? Html,
    string? Rtf,
    IReadOnlyList<string> Files,
    bool Excluded,
    IReadOnlyList<string> LostFormats,
    bool Empty);

/// <summary>还原的裁决。排在前面的理由更要紧：同时满足几条时说最要紧的那条。</summary>
public enum RestoreVerdict
{
    Restore,

    /// <summary>带排除标记：写回时标记会丢，不还。</summary>
    SkipExcluded,

    /// <summary>有写不回去的格式（图片之类），或根本没有可写的内容：不还。</summary>
    SkipUnrestorableFormat,

    /// <summary>写入之后剪贴板序列号变了：用户复制了别的东西，不去覆盖它。</summary>
    SkipChangedSinceWrite,
}

/// <summary>一个剪贴板格式在快照与还原里的角色。</summary>
public enum ClipboardFormatRole
{
    /// <summary>拾语的写入器能写回去的：文本、HTML、RTF、文件列表。</summary>
    Restored,

    /// <summary>系统按文本自动合成的（CF_TEXT、CF_OEMTEXT、CF_LOCALE）：写回文本时它们会自己再生出来。</summary>
    Synthesized,

    /// <summary>排除标记，单独处理（见 <see cref="ClipboardBackup.Excluded"/>）。</summary>
    ExclusionMarker,

    /// <summary>不携带用户内容的辅助格式（来源 URL、拖放效应、外壳偏移……）：丢了无损。</summary>
    Incidental,

    /// <summary>携带内容却写不回去：图片、位图、图元文件、音频、虚拟文件。</summary>
    Lost,
}

/// <summary>
/// 剪贴板格式的分类（纯函数，Windows 层枚举出 (id, 名字) 交给它）。
///
/// 只把已知"携带内容而写不回去"的格式认作 <see cref="ClipboardFormatRole.Lost"/>，其余认不出的
/// 都当辅助格式——浏览器、IDE、Office 复制文本时会附一堆私有格式，一律当"写不回去"就等于
/// 永远不还原。代价是个别应用的私有格式会在还原时丢掉；原来的内容仍在拾语的历史里。
/// </summary>
public static class ClipboardFormats
{
    private const uint CfText = 1;
    private const uint CfBitmap = 2;
    private const uint CfMetafilePict = 3;
    private const uint CfTiff = 6;
    private const uint CfOemText = 7;
    private const uint CfDib = 8;
    private const uint CfPalette = 9;
    private const uint CfPenData = 10;
    private const uint CfRiff = 11;
    private const uint CfWave = 12;
    private const uint CfUnicodeText = 13;
    private const uint CfEnhMetafile = 14;
    private const uint CfHdrop = 15;
    private const uint CfLocale = 16;
    private const uint CfDibV5 = 17;

    private const string HtmlFormat = "HTML Format";
    private const string RtfFormat = "Rich Text Format";

    /// <summary>与剪贴板监听遵守的两个标记同名（WindowsClipboardMonitor）。</summary>
    public const string ExcludeFromMonitorsFormat = "ExcludeClipboardContentFromMonitorProcessing";

    public const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";

    private static readonly string[] LostNames =
    [
        "PNG", "image/png", "image/jpeg", "image/gif", "image/bmp", "JFIF", "GIF",
        "FileContents", "FileGroupDescriptor", "FileGroupDescriptorW",
    ];

    private static bool Is(string? name, string wanted)
        => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase);

    public static ClipboardFormatRole Classify(uint format, string? registeredName)
    {
        switch (format)
        {
            case CfUnicodeText or CfHdrop:
                return ClipboardFormatRole.Restored;

            case CfText or CfOemText or CfLocale:
                return ClipboardFormatRole.Synthesized;

            case CfBitmap or CfMetafilePict or CfTiff or CfDib or CfPalette or CfPenData
                or CfRiff or CfWave or CfEnhMetafile or CfDibV5:
                return ClipboardFormatRole.Lost;
        }

        if (Is(registeredName, HtmlFormat) || Is(registeredName, RtfFormat))
        {
            return ClipboardFormatRole.Restored;
        }

        if (IsExclusionMarker(registeredName))
        {
            return ClipboardFormatRole.ExclusionMarker;
        }

        return registeredName is not null && LostNames.Any(name => Is(registeredName, name))
            ? ClipboardFormatRole.Lost
            : ClipboardFormatRole.Incidental;
    }

    public static bool IsExclusionMarker(string? registeredName)
        => Is(registeredName, ExcludeFromMonitorsFormat) || Is(registeredName, CanIncludeInHistoryFormat);

    /// <summary>
    /// 一列格式里写不回去的那些，按出现顺序给名字（标准格式给 CF_ 名，注册格式给它自己的名字）。
    /// </summary>
    public static IReadOnlyList<string> LostAmong(IEnumerable<(uint Id, string? Name)> formats)
        => formats
            .Where(format => Classify(format.Id, format.Name) == ClipboardFormatRole.Lost)
            .Select(format => DisplayName(format.Id, format.Name))
            .ToList();

    /// <summary>
    /// "Preferred DropEffect" 里带着移动（剪切）：只写回文件列表会把"移动"悄悄变成"复制"，
    /// 所以按写不回去算。复制（1）、链接（4）与两者之和没有这个问题。
    /// </summary>
    public static bool IsMoveDropEffect(uint effect) => (effect & 2) != 0;

    private static string DisplayName(uint id, string? name) => id switch
    {
        CfBitmap => "CF_BITMAP",
        CfMetafilePict => "CF_METAFILEPICT",
        CfTiff => "CF_TIFF",
        CfDib => "CF_DIB",
        CfPalette => "CF_PALETTE",
        CfPenData => "CF_PENDATA",
        CfRiff => "CF_RIFF",
        CfWave => "CF_WAVE",
        CfEnhMetafile => "CF_ENHMETAFILE",
        CfDibV5 => "CF_DIBV5",
        _ => name is { Length: > 0 } ? name : $"0x{id:X}",
    };
}

/// <summary>还原的判定（纯函数）。</summary>
public static class ClipboardRestore
{
    /// <param name="sequenceAfterWrite">写入拾语的译文之后立刻记下的剪贴板序列号。</param>
    /// <param name="sequenceNow">还原那一刻的序列号。</param>
    public static RestoreVerdict Decide(ClipboardBackup backup, uint sequenceAfterWrite, uint sequenceNow)
    {
        if (backup.Excluded)
        {
            return RestoreVerdict.SkipExcluded;
        }

        // 有写不回去的格式：还原出来的是残缺的一份。再看是不是连可写的内容都没有
        // （比如只有 HTML、没有纯文本——写入器写 HTML 要带着纯文本）。
        if (backup.LostFormats.Count > 0
            || (!backup.Empty && backup.Text is null && backup.Files.Count == 0))
        {
            return RestoreVerdict.SkipUnrestorableFormat;
        }

        // 序列号比 Xtranslate 的文本比对准：用户在这期间复制了别的东西，就不去覆盖它。
        return sequenceNow == sequenceAfterWrite
            ? RestoreVerdict.Restore
            : RestoreVerdict.SkipChangedSinceWrite;
    }
}
