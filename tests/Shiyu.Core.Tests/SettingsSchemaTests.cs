using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// The settings surface is data, so its integrity is testable as data: ids
/// unique, parents real, numbers bounded and carrying units, segmented
/// choices present, keywords there for the search that is coming — the
/// checks that would otherwise only surface as a broken page at runtime.
///
/// The five-page placement (ticket 23, UI report §5.1) lives here as a table
/// too: every item of the pre-redesign tree, plus the additions §5.1 asked
/// for, on the page the report assigns it to — the "全部落位" acceptance as
/// a script rather than a screenshot.
/// </summary>
public class SettingsSchemaTests
{
    private static IEnumerable<SettingsItem> Items()
        => SettingsSchema.Tree.SelectMany(page => page.Sections).SelectMany(section => section.Items);

    /// <summary>§5.1 的落位表：item Id → 页 Id。新增项照列（hotkey.library 等）。</summary>
    private static readonly IReadOnlyDictionary<string, string> Placement =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 常规：开机自启不再挂在“数据”下，轻量模式不再挂在“密度”下。
            ["store.start-with-windows"] = "general",
            ["theme"] = "general",
            ["look.lightweight"] = "general",
            ["store.directory"] = "general",
            ["store.usage"] = "general",
            ["store.backup"] = "general",

            // 窄条：原“动作”页并入；密度随行。
            ["bar.at-cursor"] = "bar",
            ["look.bar-topmost"] = "bar",
            ["bar.text-lines"] = "bar",
            ["bar.image-height"] = "bar",
            ["bar.file-count"] = "bar",
            ["look.preview-on-hover"] = "bar",
            ["look.preview-hover"] = "bar",
            ["bar.card-tooltips"] = "bar",
            ["bar.actions"] = "bar",
            ["action.sound"] = "bar",

            // 记录与隐私：记录、排除三层、保留与保护同页。
            ["record.text"] = "privacy",
            ["record.images"] = "privacy",
            ["record.files"] = "privacy",
            ["exclusions"] = "privacy",
            ["store.retention-days"] = "privacy",
            ["store.protect"] = "privacy",
            ["store.protect-favorites"] = "privacy",
            ["store.protect-pinned"] = "privacy",

            // 翻译：原“服务”页；徽标开关与快捷键引用卡跟来。
            ["service.target-language"] = "translate",
            ["service.source-language"] = "translate",
            ["service.backend-kind"] = "translate",
            ["service.preset"] = "translate",
            ["service.base-url"] = "translate",
            ["service.model"] = "translate",
            ["service.api-key"] = "translate",
            ["translate.templates"] = "translate",
            ["translate.auto-copy"] = "translate",
            ["hotkeys.selection-badge"] = "translate",
            ["translate.hotkey-ref"] = "translate",

            // 快捷键：只放按键；Win+V 是快速粘贴的子项；管理窗键新增；
            // 窗口内按键速查（§5.2 键位即数据，票 25）。
            ["hotkey.bar"] = "hotkeys",
            ["hotkey.quickbar"] = "hotkeys",
            ["winv.takeover"] = "hotkeys",
            ["hotkey.capture"] = "hotkeys",
            ["hotkey.clipboard"] = "hotkeys",
            ["hotkey.library"] = "hotkeys",
            ["hotkey.reverse"] = "hotkeys",
            ["hotkeys.cheatsheet"] = "hotkeys",
            ["hotkeys.reset"] = "hotkeys",

            // 关于：品牌、更新、帮助、隐私。
            ["about.brand"] = "about",
            ["about.version"] = "about",
            ["about.update-auto"] = "about",
            ["about.update-check"] = "about",
            ["about.onboarding"] = "about",
            ["about.logs"] = "about",
            ["about.privacy-note"] = "about",
        };

    [Fact]
    public void Every_item_lands_on_the_page_the_report_assigns()
    {
        foreach (var (id, page) in Placement)
        {
            Assert.Equal(page, SettingsSchema.FindPageOf(id)?.Id);
        }
    }

    [Fact]
    public void The_placement_table_covers_the_whole_tree()
    {
        // 反向核对：树里没有落位表不知道的项，落位表也没有指空项。
        var ids = Items().Select(item => item.Id).ToHashSet();
        Assert.Equal(ids.Count, Placement.Count);
        Assert.True(ids.SetEquals(Placement.Keys));
    }

    [Fact]
    public void Every_item_id_is_unique()
    {
        var ids = Items().Select(item => item.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void A_parent_exists_and_hides_children_when_off()
    {
        var byId = Items().ToDictionary(item => item.Id);
        foreach (var child in Items().Where(item => item.Parent is not null))
        {
            var parent = byId.TryGetValue(child.Parent!, out var found) ? found : null;
            Assert.NotNull(parent);

            // 父项要么是开关、要么是热键（快速粘贴为空时“也用 Win+V”没有
            // 意义）、要么是分段（翻译方式停在公共通道时自备密钥四行收起）
            // ——三类都能回答“孩子该不该露面”。
            Assert.Contains(
                parent!.Control,
                new[] { SettingsControl.Toggle, SettingsControl.Hotkey, SettingsControl.Segmented });
        }
    }

    [Fact]
    public void The_own_key_rows_fold_under_the_backend_choice()
    {
        // §5.1：自备密钥的预设/地址/模型/凭据是“翻译方式”选了自备密钥才有
        // 意义的四个子行——同一张组卡、同一份可见性裁决。
        foreach (var id in new[] { "service.preset", "service.base-url", "service.model", "service.api-key" })
        {
            Assert.Equal("service.backend-kind", Items().Single(i => i.Id == id).Parent);
        }
    }

    [Fact]
    public void Numbers_are_bounded_sensibly_and_carry_a_unit()
    {
        foreach (var item in Items().Where(item => item.Control == SettingsControl.Number))
        {
            Assert.True(item.Min < item.Max, $"{item.Id} has no meaningful range");
            Assert.False(
                string.IsNullOrWhiteSpace(item.Unit),
                $"{item.Id} shows no unit — every number needs one (U-14)");
        }
    }

    [Fact]
    public void Segmented_and_choice_items_offer_choices()
    {
        foreach (var item in Items().Where(item =>
                     item.Control is SettingsControl.Segmented or SettingsControl.Choice))
        {
            Assert.True(item.ChoiceList.Length >= 2, $"{item.Id} needs choices to pick from");
        }
    }

    [Fact]
    public void Every_item_is_searchable_by_label_or_keyword()
    {
        foreach (var item in Items())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(item.Label) && item.KeywordList.Length == 0,
                $"{item.Id} has neither label nor keywords");
        }
    }

    [Fact]
    public void Pages_carry_copy_that_names_user_intent()
    {
        var titles = SettingsSchema.Tree.Select(page => page.Title).ToList();

        // §5.1 五页 + 关于固定底部：页名用用户要找的东西命名。
        Assert.Equal(
            ["常规", "窄条", "记录与隐私", "翻译", "快捷键", "关于"],
            titles);
        Assert.DoesNotContain("服务", titles);
        Assert.DoesNotContain("高级", titles);
        Assert.All(SettingsSchema.Tree, page => Assert.NotEmpty(page.Sections));
    }

    [Fact]
    public void The_old_settings_surface_is_covered_by_the_tree()
    {
        var ids = Items().Select(item => item.Id).ToHashSet();

        Assert.Contains("theme", ids);
        Assert.Contains("hotkey.capture", ids);
        Assert.Contains("hotkey.clipboard", ids);
        Assert.Contains("hotkey.quickbar", ids);
        Assert.Contains("hotkey.bar", ids);
        Assert.Contains("bar.text-lines", ids);
        Assert.Contains("bar.image-height", ids);
        Assert.Contains("bar.file-count", ids);
        Assert.Contains("bar.actions", ids);
        Assert.Contains("action.sound", ids);
        Assert.Contains("service.target-language", ids);
        Assert.Contains("service.source-language", ids);
        Assert.Contains("service.base-url", ids);
        Assert.Contains("service.model", ids);
        Assert.Contains("service.api-key", ids);
        Assert.Contains("store.retention-days", ids);
        Assert.Contains("store.protect", ids);
        Assert.Contains("store.protect-favorites", ids);
        Assert.Contains("store.protect-pinned", ids);
        Assert.Contains("store.directory", ids);
        Assert.Contains("store.start-with-windows", ids);
        Assert.Contains("exclusions", ids);
    }

    [Fact]
    public void Old_page_ids_resolve_through_the_alias_table()
    {
        Assert.Equal("privacy", SettingsSchema.ResolvePage("record")?.Id);
        Assert.Equal("bar", SettingsSchema.ResolvePage("actions")?.Id);
        Assert.Equal("bar", SettingsSchema.ResolvePage("look")?.Id);
        Assert.Equal("translate", SettingsSchema.ResolvePage("service")?.Id);
        Assert.Equal("general", SettingsSchema.ResolvePage("store")?.Id);

        // 新页名与未知页名：前者直达，后者为 null——别名兜底不是别名凭空。
        Assert.Equal("general", SettingsSchema.ResolvePage("general")?.Id);
        Assert.Null(SettingsSchema.ResolvePage("no-such-page"));
    }

    [Fact]
    public void Item_deep_links_resolve_to_a_real_page()
    {
        foreach (var item in Items())
        {
            Assert.NotNull(SettingsSchema.FindPageOf(item.Id));
        }
    }

    [Fact]
    public void The_credential_item_is_a_password_and_never_a_plain_text_box()
    {
        var credential = Items().Single(item => item.Id == "service.api-key");
        Assert.Equal(SettingsControl.Password, credential.Control);
    }

    [Fact]
    public void The_theme_is_a_segmented_control_not_a_dropdown()
    {
        var theme = Items().Single(item => item.Id == "theme");
        Assert.Equal(SettingsControl.Segmented, theme.Control);
        Assert.Equal(3, theme.ChoiceList.Length);
    }

    [Fact]
    public void Languages_are_picked_not_typed()
    {
        // §3.9 P1：语言曾是自由文本框。现在是下拉，源语言首项“自动检测”。
        var target = Items().Single(item => item.Id == "service.target-language");
        var source = Items().Single(item => item.Id == "service.source-language");
        Assert.Equal(SettingsControl.Choice, target.Control);
        Assert.Equal(SettingsControl.Choice, source.Control);
        Assert.Equal("自动检测", source.ChoiceList[0]);
    }

    [Fact]
    public void The_reference_card_points_at_a_real_hotkey()
    {
        var reference = Items().Single(item => item.Id == "translate.hotkey-ref");
        Assert.Equal(SettingsControl.Link, reference.Control);
        Assert.NotNull(SettingsSchema.Find("hotkey.capture"));
    }

    [Fact]
    public void Delete_protection_children_collapse_under_the_master_toggle()
    {
        var favorites = Items().Single(item => item.Id == "store.protect-favorites");
        var pinned = Items().Single(item => item.Id == "store.protect-pinned");
        Assert.Equal("store.protect", favorites.Parent);
        Assert.Equal("store.protect", pinned.Parent);
    }

    [Fact]
    public void Winv_takeover_sits_under_the_quick_paste_hotkey()
    {
        // §5.1：“快速粘贴（子项：也用 Win+V 呼出）”——接管说明紧挨着它
        // 接管的那个键，不再隔一个页签。
        var takeover = Items().Single(item => item.Id == "winv.takeover");
        Assert.Equal("hotkey.quickbar", takeover.Parent);
        Assert.Equal("hotkeys", SettingsSchema.FindPageOf(takeover.Id)!.Id);
    }

    [Fact]
    public void The_hover_delay_folds_under_the_hover_preview_switch()
    {
        // 用户需求 2026-10-05：悬停自动预览是开关，延迟只在开着时有意义——
        // 关掉开关，延迟那行随之收起，不再靠"填 0"表达关闭。
        var delay = Items().Single(item => item.Id == "look.preview-hover");
        Assert.Equal("look.preview-on-hover", delay.Parent);
        Assert.True(delay.Min > 0, "zero no longer means off; the switch does");
        Assert.Equal(
            SettingsControl.Toggle,
            Items().Single(item => item.Id == "look.preview-on-hover").Control);
    }

    [Fact]
    public void The_protection_master_is_on_by_default()
    {
        Assert.True(new AppSettings().ProtectEntries);
    }

    [Fact]
    public void The_bar_comes_up_beside_the_cursor_by_default()
    {
        Assert.True(new AppSettings().BarAtCursor);
        Assert.Contains(
            SettingsSchema.Tree.SelectMany(p => p.Sections).SelectMany(s => s.Items),
            item => item.Id == "bar.at-cursor" && item.Control == SettingsControl.Toggle);
    }

    [Fact]
    public void The_bar_is_pinned_to_the_top_by_default_and_the_setting_lives_with_the_bar()
    {
        // 置顶 is how the resident bar works by default; the switch that turns
        // it off belongs with the bar's own page (ticket 39, then §5.1).
        Assert.True(new AppSettings().BarAlwaysOnTop);

        var item = Items().Single(i => i.Id == "look.bar-topmost");
        Assert.Equal(SettingsControl.Toggle, item.Control);
        Assert.Equal("bar", SettingsSchema.FindPageOf(item.Id)!.Id);
    }

    [Fact]
    public void The_selection_badge_is_off_by_default_and_its_switch_lives_with_translation()
    {
        // 默认关闭是票 37 的硬约束：全局鼠标钩子的开销不为用户决定。
        // 开关跟翻译走（§5.1：徽标是翻译的触发方式），与划词热键互为近邻。
        Assert.False(new AppSettings().SelectionBadge);

        var item = Items().Single(i => i.Id == "hotkeys.selection-badge");
        Assert.Equal(SettingsControl.Toggle, item.Control);
        Assert.Equal("translate", SettingsSchema.FindPageOf(item.Id)!.Id);
    }

    [Fact]
    public void The_library_hotkey_is_off_by_default_and_parses_to_nothing()
    {
        // §5.1 快捷键页新增“打开管理窗”，默认不设：空串不注册、不报错。
        Assert.Equal(string.Empty, new AppSettings().LibraryHotkey);

        var (bindings, problems) = HotkeyPlan.Build(new AppSettings());
        Assert.Empty(problems);
        Assert.DoesNotContain(bindings, b => b.Action == HotkeyAction.Library);
    }

    [Fact]
    public void The_reverse_input_hotkey_lives_on_the_hotkeys_page_and_warns_about_Microsoft_365()
    {
        // 票 43：Alt+Q 在 Microsoft 365（Word、Excel、PowerPoint、Outlook）里是"跳到搜索框"。
        // 那是应用内快捷键，RegisterHotKey 不会报冲突，注册后会悄悄把它盖掉——所以 Hint 要写明，
        // 并告诉用户可以自己改键。
        var item = Items().Single(i => i.Id == "hotkey.reverse");

        Assert.Equal(SettingsControl.Hotkey, item.Control);
        Assert.Equal("hotkeys", SettingsSchema.FindPageOf(item.Id)!.Id);
        Assert.Equal("反向输入", item.Label);
        Assert.Contains("Microsoft 365", item.Hint);
        Assert.Contains("Alt+Q", item.Hint);
        Assert.Contains("改键", item.Hint);
    }

    [Fact]
    public void The_reverse_input_hotkey_can_be_found_by_searching_for_what_it_does()
    {
        foreach (var query in new[] { "反向输入", "回帖", "输入框", "Alt+Q" })
        {
            Assert.Contains(SettingsSearch.Find(query), hit => hit.Item.Id == "hotkey.reverse");
        }
    }

    [Fact]
    public void The_templates_card_says_it_serves_the_reverse_input_too_and_names_both_defaults()
    {
        // 「提示词模板」控件的说明里不能还只说"面板"：反向输入框（票 43）也读它，
        // 并且有自己的默认（"输入框默认"那一列）。
        var item = Items().Single(i => i.Id == "translate.templates");

        Assert.Contains("反向输入框", item.Hint);
        Assert.Contains("面板默认", item.Hint);
        Assert.Contains("输入框默认", item.Hint);
        Assert.Contains("反向输入", string.Join(' ', item.KeywordList));
    }

    [Fact]
    public void Searching_for_the_reverse_input_default_lands_on_the_templates_card()
    {
        Assert.Contains(SettingsSearch.Find("输入框默认"), hit => hit.Item.Id == "translate.templates");
    }
}
