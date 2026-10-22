namespace Shiyu.Core;

/// <summary>
/// The narrow bar's keyboard decisions. In Core because the rules are the
/// product: which layer Escape peels next, how filters cycle. The window
/// supplies the state; this decides what that state means.
/// </summary>
public static class BarKeyboard
{
    public enum EscapeAction
    {
        /// <summary>The innermost layer: whatever preview is open goes away.</summary>
        ClosePreview,

        /// <summary>The search query empties, list returns to full.</summary>
        ClearQuery,

        ClearSubtypeFilter,

        ClearTagFilter,

        ClearTypeFilter,

        /// <summary>The ★-only toggle turns off.</summary>
        ClearFavoriteFilter,

        ClearGroupFilter,

        HideWindow,
    }

    /// <summary>
    /// Escape undoes the most recent step, not the whole session: the preview,
    /// then each filter layer from most-recently-touched to coarsest (query,
    /// subtype, tag, type, favourite, group), and only when every layer is
    /// clear does it hide the window. The footer's "Esc …" hint reads the same
    /// stack, so what the text promises is what the key does.
    /// </summary>
    public static EscapeAction NextEscape(
        bool previewOpen,
        bool hasQuery,
        bool hasSubtypeFilter,
        bool hasTagFilter,
        bool hasTypeFilter,
        bool hasFavoriteFilter,
        bool hasGroupFilter)
    {
        if (previewOpen)
        {
            return EscapeAction.ClosePreview;
        }

        if (hasQuery)
        {
            return EscapeAction.ClearQuery;
        }

        if (hasSubtypeFilter)
        {
            return EscapeAction.ClearSubtypeFilter;
        }

        if (hasTagFilter)
        {
            return EscapeAction.ClearTagFilter;
        }

        if (hasTypeFilter)
        {
            return EscapeAction.ClearTypeFilter;
        }

        if (hasFavoriteFilter)
        {
            return EscapeAction.ClearFavoriteFilter;
        }

        if (hasGroupFilter)
        {
            return EscapeAction.ClearGroupFilter;
        }

        return EscapeAction.HideWindow;
    }

    /// <summary>Cycles an index through a list of choices, wrapping at both ends.</summary>
    public static int Cycle(int current, int delta, int count)
        => count <= 1 ? current : ((current + delta) % count + count) % count;

    // --- 按键归属（用户需求 2026-10-22：选中卡片按 P 置顶，P 却打进了搜索框） -----------------
    //
    // 窄条有两种状态，看你最近一次在做什么：搜（焦点在搜索框，按键是打字）与挑（点了卡片、或搜索框
    // 空着时按 ↑↓，焦点在列表，字母是卡片动作）。Ctrl+字母两种状态下都是动作——按住 Ctrl 看到的
    // 键帽就是这么教的。

    /// <summary>键盘焦点此刻在哪。</summary>
    public enum Focus
    {
        /// <summary>挑：焦点在卡片列表。</summary>
        List,

        /// <summary>搜：焦点在搜索框。</summary>
        Search,

        /// <summary>别的输入框：按键全归它，卡片动作一个都不接。</summary>
        OtherField,
    }

    /// <summary>
    /// 卡片动作字母（C/O/P/S/N/G/D/Z）这一下是动作还是打字。挑的时候单按、Ctrl+ 都是动作；搜的时候只有
    /// Ctrl+ 是——但搜索框里选着字时 Ctrl+C 留给复制那段字。带 Shift/Alt 的组合不是卡片动作。
    /// </summary>
    public static bool LetterActs(Focus focus, bool plain, bool controlOnly, bool searchHasSelection, bool isCopy)
        => focus switch
        {
            Focus.List => plain || controlOnly,
            Focus.Search => controlOnly && !(isCopy && searchHasSelection),
            _ => false,
        };

    /// <summary>
    /// 数字键这一下是编号粘贴还是打字：挑的时候、搜索框空着时是粘贴；搜索框里已经有字时是打字（能搜
    /// 「2024」、电话号码），要粘贴就 Ctrl+数字。
    /// </summary>
    public static bool DigitPastes(Focus focus, bool hasQuery, bool controlOnly)
        => focus switch
        {
            Focus.List => true,
            Focus.Search => controlOnly || !hasQuery,
            _ => false,
        };

    /// <summary>←→ 换类型、按住 Space 预览：挑的时候，或搜索框空着时；搜到一半它们留给光标与空格。</summary>
    public static bool NavigationActs(Focus focus, bool hasQuery)
        => focus == Focus.List || (focus == Focus.Search && !hasQuery);

    /// <summary>↑↓ 选卡片之后转进「挑」：只在搜索框空着时——搜到一半的人还会接着打字。</summary>
    public static bool ArrowStartsPicking(Focus focus, bool hasQuery) => focus == Focus.Search && !hasQuery;
}
