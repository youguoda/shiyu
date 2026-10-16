namespace Shiyu.Core;

/// <summary>
/// 多选时用户选中的先后（评审 2026-10-16 F-12「粘贴全部」按选中顺序）。列表控件自己的选中集靠不住：
/// 范围选、全选、重载之后都按列表顺序重排。这里只跟用户的点选走，重载期间由调用方暂停记录。
/// </summary>
public sealed class SelectionOrder
{
    private readonly List<long> _order = [];

    /// <summary>这几条刚被选上：没记过的按到来的次序排到末尾。</summary>
    public void Add(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            if (!_order.Contains(id))
            {
                _order.Add(id);
            }
        }
    }

    /// <summary>这几条被取消选中。</summary>
    public void Remove(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            _order.Remove(id);
        }
    }

    /// <summary>
    /// 此刻选中的这些按选中先后排；没记到先后的（暂停记录期间补回的）按传进来的次序排在后面。
    /// 记过、但已不在选中里的顺手忘掉。
    /// </summary>
    public IReadOnlyList<long> Arrange(IReadOnlyList<long> selected)
    {
        var current = selected.ToHashSet();
        _order.RemoveAll(id => !current.Contains(id));
        return [.. _order, .. selected.Where(id => !_order.Contains(id))];
    }
}

/// <param name="Text">要贴出去的整段；没有可贴的为空串。</param>
/// <param name="Pasted">贴了几条。</param>
/// <param name="SkippedImages">跳过了几张图片：图片贴不成文字。</param>
public sealed record BatchPasteText(string Text, int Pasted, int SkippedImages);

/// <summary>
/// 「粘贴全部」贴出去的文字（评审 2026-10-16 F-12）：按给定次序，一条一行（多行的条目保持原样，
/// 条目之间只隔一个换行）；文件条目贴它们的路径，每个一行；图片贴不成文字，跳过并报数。
/// </summary>
public static class BatchPaste
{
    public static BatchPasteText Join(IEnumerable<Entry> entries, string newline = "\r\n")
    {
        var parts = new List<string>();
        var skipped = 0;
        foreach (var entry in entries)
        {
            var part = entry.Kind switch
            {
                EntryKind.Image => null,
                EntryKind.Files => string.Join(newline, entry.Files),
                _ => entry.Text.TrimEnd('\r', '\n'),
            };

            if (entry.Kind == EntryKind.Image)
            {
                skipped++;
            }
            else if (part is { Length: > 0 })
            {
                parts.Add(part);
            }
        }

        return new BatchPasteText(string.Join(newline, parts), parts.Count, skipped);
    }
}
