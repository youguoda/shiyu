using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// Settings search has to find what the user would actually type — product
/// words in the labels, user words in the hand-written keywords — cap itself
/// before it floods the screen, and say where each hit lives.
/// </summary>
public class SettingsSearchTests
{
    [Fact]
    public void Searching_the_users_words_finds_the_retention_setting()
    {
        var hits = SettingsSearch.Find("多久删");

        var hit = Assert.Single(hits);
        Assert.Equal("store.retention-days", hit.Item.Id);
        Assert.Equal("记录与隐私", hit.PageTitle);
        Assert.Equal("保留与保护", hit.SectionTitle);
    }

    [Fact]
    public void A_label_word_matches_without_knowing_keywords()
    {
        Assert.Contains(SettingsSearch.Find("凭据"), hit => hit.Item.Id == "service.api-key");
        Assert.Contains(SettingsSearch.Find("主题"), hit => hit.Item.Id == "theme");
    }

    [Fact]
    public void Keywords_reach_beyond_labels()
    {
        Assert.Contains(SettingsSearch.Find("夜间模式"), hit => hit.Item.Id == "theme");
        Assert.Contains(SettingsSearch.Find("误删"), hit => hit.Item.Id == "store.protect");
        Assert.Contains(SettingsSearch.Find("迁移"), hit => hit.Item.Id == "store.directory");
    }

    [Fact]
    public void Multiple_words_narrow_rather_than_flood()
    {
        var oneWord = SettingsSearch.Find("保护");
        var twoWords = SettingsSearch.Find("保护 收藏");

        Assert.Contains(twoWords, hit => hit.Item.Id == "store.protect-favorites");
        Assert.True(twoWords.Count <= oneWord.Count);
        Assert.DoesNotContain(twoWords, hit => hit.Item.Id == "store.protect-pinned");
    }

    [Fact]
    public void Results_carry_their_page_and_section()
    {
        var hit = SettingsSearch.Find("快捷键").Single(item => item.Item.Id == "hotkey.quickbar");

        Assert.Equal("快捷键", hit.PageTitle);
        Assert.Equal("全局", hit.SectionTitle);
    }

    [Fact]
    public void Results_never_exceed_the_cap_whatever_the_query()
    {
        // The tree today is small, so no real query floods it yet; the cap
        // still has to hold for the widest queries there are, and it will
        // keep holding the day the tree grows past it.
        foreach (var broad in new[] { "的", "设置", "拾语", "a" })
        {
            Assert.True(SettingsSearch.Find(broad).Count <= SettingsSearch.ResultCap);
        }
    }

    [Fact]
    public void Gibberish_finds_nothing_and_that_is_the_answer()
    {
        Assert.Empty(SettingsSearch.Find("zzzqqqxxx"));
    }

    [Fact]
    public void Blank_input_is_no_search_at_all()
    {
        Assert.Empty(SettingsSearch.Find("   "));
    }
}
