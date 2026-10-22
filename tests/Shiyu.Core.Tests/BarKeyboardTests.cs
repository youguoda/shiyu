using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// Escape is a stack of "undo my last step", not a close button — the
/// decision of which layer gives way next is the part worth pinning down,
/// because getting it wrong is what makes users afraid of the key.
/// </summary>
public class BarKeyboardTests
{
    [Fact]
    public void Escape_peels_every_filter_layer_before_hiding()
    {
        // The full stack: preview → query → subtype → tag → type → favourite
        // → group → hide. Every layer the footer can promise has a peel.
        Assert.Equal(BarKeyboard.EscapeAction.ClosePreview,
            BarKeyboard.NextEscape(previewOpen: true, hasQuery: true, hasSubtypeFilter: true,
                hasTagFilter: true, hasTypeFilter: true, hasFavoriteFilter: true, hasGroupFilter: true));
        Assert.Equal(BarKeyboard.EscapeAction.ClearQuery,
            BarKeyboard.NextEscape(previewOpen: false, hasQuery: true, hasSubtypeFilter: true,
                hasTagFilter: true, hasTypeFilter: true, hasFavoriteFilter: true, hasGroupFilter: true));
        Assert.Equal(BarKeyboard.EscapeAction.ClearSubtypeFilter,
            BarKeyboard.NextEscape(previewOpen: false, hasQuery: false, hasSubtypeFilter: true,
                hasTagFilter: true, hasTypeFilter: true, hasFavoriteFilter: true, hasGroupFilter: true));
        Assert.Equal(BarKeyboard.EscapeAction.ClearTagFilter,
            BarKeyboard.NextEscape(previewOpen: false, hasQuery: false, hasSubtypeFilter: false,
                hasTagFilter: true, hasTypeFilter: true, hasFavoriteFilter: true, hasGroupFilter: true));
        Assert.Equal(BarKeyboard.EscapeAction.ClearTypeFilter,
            BarKeyboard.NextEscape(previewOpen: false, hasQuery: false, hasSubtypeFilter: false,
                hasTagFilter: false, hasTypeFilter: true, hasFavoriteFilter: true, hasGroupFilter: true));
        Assert.Equal(BarKeyboard.EscapeAction.ClearFavoriteFilter,
            BarKeyboard.NextEscape(previewOpen: false, hasQuery: false, hasSubtypeFilter: false,
                hasTagFilter: false, hasTypeFilter: false, hasFavoriteFilter: true, hasGroupFilter: true));
        Assert.Equal(BarKeyboard.EscapeAction.ClearGroupFilter,
            BarKeyboard.NextEscape(previewOpen: false, hasQuery: false, hasSubtypeFilter: false,
                hasTagFilter: false, hasTypeFilter: false, hasFavoriteFilter: false, hasGroupFilter: true));
        Assert.Equal(BarKeyboard.EscapeAction.HideWindow,
            BarKeyboard.NextEscape(previewOpen: false, hasQuery: false, hasSubtypeFilter: false,
                hasTagFilter: false, hasTypeFilter: false, hasFavoriteFilter: false, hasGroupFilter: false));
    }

    [Fact]
    public void Cycling_the_type_filter_wraps_around()
    {
        Assert.Equal(1, BarKeyboard.Cycle(0, delta: 1, count: 3));
        Assert.Equal(0, BarKeyboard.Cycle(2, delta: 1, count: 3));
        Assert.Equal(2, BarKeyboard.Cycle(0, delta: -1, count: 3));
    }

    [Fact]
    public void Cycling_a_single_choice_lands_where_it_started()
    {
        Assert.Equal(0, BarKeyboard.Cycle(0, delta: 1, count: 1));
    }

    // 按键归属（用户实录 2026-10-22：选中卡片按 P 置顶，P 却打进了搜索框）。

    [Theory]
    // 挑（点了卡片）：单按、Ctrl+ 都是动作。
    [InlineData(BarKeyboard.Focus.List, true, false, false, false, true)]
    [InlineData(BarKeyboard.Focus.List, false, true, false, false, true)]
    // 搜：单按是打字，Ctrl+ 是动作。
    [InlineData(BarKeyboard.Focus.Search, true, false, false, false, false)]
    [InlineData(BarKeyboard.Focus.Search, false, true, false, false, true)]
    // 搜索框里选着字：Ctrl+C 复制那段字；别的 Ctrl+字母照样是动作。
    [InlineData(BarKeyboard.Focus.Search, false, true, true, true, false)]
    [InlineData(BarKeyboard.Focus.Search, false, true, true, false, true)]
    // 带 Shift/Alt 的组合（既不是单按也不是只按 Ctrl）不是卡片动作。
    [InlineData(BarKeyboard.Focus.List, false, false, false, false, false)]
    // 别的输入框：一个都不接。
    [InlineData(BarKeyboard.Focus.OtherField, false, true, false, false, false)]
    public void A_card_letter_acts_only_where_it_cannot_be_typing(
        BarKeyboard.Focus focus, bool plain, bool controlOnly, bool selection, bool isCopy, bool acts)
        => Assert.Equal(acts, BarKeyboard.LetterActs(focus, plain, controlOnly, selection, isCopy));

    [Theory]
    [InlineData(BarKeyboard.Focus.List, true, false, true)]       // 挑：编号粘贴
    [InlineData(BarKeyboard.Focus.Search, false, false, true)]    // 搜索框空着：编号粘贴
    [InlineData(BarKeyboard.Focus.Search, true, false, false)]    // 搜到一半：数字是打字
    [InlineData(BarKeyboard.Focus.Search, true, true, true)]      // 搜到一半要粘贴：Ctrl+数字
    [InlineData(BarKeyboard.Focus.OtherField, false, false, false)]
    public void A_digit_pastes_unless_a_query_is_being_typed(
        BarKeyboard.Focus focus, bool hasQuery, bool controlOnly, bool pastes)
        => Assert.Equal(pastes, BarKeyboard.DigitPastes(focus, hasQuery, controlOnly));

    [Fact]
    public void Arrows_start_picking_only_from_an_empty_search()
    {
        Assert.True(BarKeyboard.ArrowStartsPicking(BarKeyboard.Focus.Search, hasQuery: false));
        Assert.False(BarKeyboard.ArrowStartsPicking(BarKeyboard.Focus.Search, hasQuery: true));
        Assert.False(BarKeyboard.ArrowStartsPicking(BarKeyboard.Focus.List, hasQuery: false));
    }

    [Fact]
    public void Kind_arrows_and_space_preview_leave_a_query_alone()
    {
        Assert.True(BarKeyboard.NavigationActs(BarKeyboard.Focus.List, hasQuery: true));
        Assert.True(BarKeyboard.NavigationActs(BarKeyboard.Focus.Search, hasQuery: false));
        Assert.False(BarKeyboard.NavigationActs(BarKeyboard.Focus.Search, hasQuery: true));
        Assert.False(BarKeyboard.NavigationActs(BarKeyboard.Focus.OtherField, hasQuery: false));
    }
}
