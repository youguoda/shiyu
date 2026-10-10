using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 键位即数据（§5.2、O-42）：表本身的完整性、与 HotkeyPlan 四键的一致、
/// 以及每个渲染函数的输出——「改一个键七处同步变」的验收在这里钉住：
/// 渲染文本全部由表与设置推导，任何一处手写都是测试要抓的漂移。
/// </summary>
public class KeyMapTests
{
    [Fact]
    public void Every_row_has_a_key_on_at_least_one_surface()
    {
        foreach (var row in KeyMap.Rows)
        {
            Assert.True(
                !string.IsNullOrWhiteSpace(row.Bar) || !string.IsNullOrWhiteSpace(row.Settings)
                || !string.IsNullOrWhiteSpace(row.Library),
                $"{row.Id} has no key on any surface — a row without a key teaches nothing");
        }
    }

    [Fact]
    public void Row_ids_are_unique()
    {
        var ids = KeyMap.Rows.Select(row => row.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void A_badge_only_extends_a_row_that_has_a_bar_key()
    {
        foreach (var row in KeyMap.Rows.Where(row => row.Badge is not null))
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Bar), $"{row.Id} badges without a bar key");
        }
    }

    [Fact]
    public void Tray_key_letters_are_single_capitals_or_full_modifiers()
    {
        // 键帽有固定的高度与最小宽度：单字母帽与 Alt+S 这样的完整修饰键都
        // 放得下，但不能出现 "Ctrl+F" 这种带功能键全文的帽——那是速查行的事。
        foreach (var actionId in new[] { "copy", "open", "pin", "favorite", "note", "group", "delete" })
        {
            var key = KeyMap.TrayKey(actionId);
            Assert.NotNull(key);
            Assert.True(
                key!.Length is 1 or 2 or 3 or 4,
                $"{actionId} keycap text {key} is not a badge");
        }
    }

    [Fact]
    public void Tray_key_answers_null_for_actions_without_a_letter()
    {
        Assert.Null(KeyMap.TrayKey("paste"));
        Assert.Null(KeyMap.TrayKey("plain"));
        Assert.Null(KeyMap.TrayKey("locate"));
        Assert.Null(KeyMap.TrayKey("more"));
    }

    [Fact]
    public void Tray_key_reads_the_same_letters_the_bar_handler_types()
    {
        // 与 BarKeyHandling 的键盘分支一一对应（处理在 App 层测不到，这里
        // 把字母钉死：改表必读测试，改键盘必看表）。
        Assert.Equal("C", KeyMap.TrayKey("copy"));
        Assert.Equal("O", KeyMap.TrayKey("open"));
        Assert.Equal("P", KeyMap.TrayKey("pin"));
        Assert.Equal("S", KeyMap.TrayKey("favorite"));
        Assert.Equal("N", KeyMap.TrayKey("note"));
        Assert.Equal("G", KeyMap.TrayKey("group"));
        Assert.Equal("D", KeyMap.TrayKey("delete"));
    }

    [Fact]
    public void Badge_text_prefers_the_short_form_over_the_full_key()
    {
        // 搜索的完整键是 Ctrl+F；帽浮出时 Ctrl 正被按住，帽上只写 F。
        Assert.Equal("F", KeyMap.BadgeText("search"));
        Assert.Equal("Alt+S", KeyMap.BadgeText("favorite-filter"));
        Assert.Equal("Tab", KeyMap.BadgeText("tag"));
        Assert.Null(KeyMap.BadgeText("no-such-row"));
    }

    [Fact]
    public void Global_combinations_read_the_live_settings_for_every_action()
    {
        var settings = new AppSettings
        {
            CaptureHotkey = "Ctrl+Alt+Q",
            QuickBarHotkey = "Ctrl+Alt+P",
            ClipboardTranslateHotkey = "Ctrl+Alt+X",
            LibraryHotkey = "Ctrl+Alt+L",
            ReverseInputHotkey = "Ctrl+Alt+R",
        };

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var key = KeyMap.Combination(action, settings);
            Assert.NotNull(key);
            Assert.Contains("Ctrl+Alt+", key);
        }
    }

    [Fact]
    public void An_unset_combination_is_null_not_an_empty_string()
    {
        Assert.Null(KeyMap.Combination(HotkeyAction.Library, new AppSettings()));
    }

    [Fact]
    public void Default_combinations_stay_in_step_with_hotkey_plan()
    {
        // KeyMap 读的是设置属性名，HotkeyPlan 注册的也是同一批属性——两处
        // 各自列举动作，此测试保证列举对得上：默认方案里的每个绑定，KeyMap
        // 都给得出同一个组合；默认不设的（管理窗），两边都给空。
        var defaults = new AppSettings();
        var (bindings, problems) = HotkeyPlan.Build(defaults);

        Assert.Empty(problems);

        var byAction = bindings.ToDictionary(binding => binding.Action, binding => binding.Spec.ToString());
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var fromMap = KeyMap.Combination(action, defaults);
            if (byAction.TryGetValue(action, out var registered))
            {
                Assert.Equal(registered, fromMap);
            }
            else
            {
                Assert.Null(fromMap);
            }
        }
    }

    [Fact]
    public void Tray_menu_lines_carry_the_live_combination_in_the_accelerator_column()
    {
        // Win32 菜单惯例：\t 之后的文本右对齐成加速键列（§5.2）。
        Assert.Equal("快速粘贴\tCtrl+Shift+V", KeyMap.TrayMenuLine(HotkeyAction.QuickBar, new AppSettings()));
        Assert.Equal("翻译剪贴板\tCtrl+Shift+X",
            KeyMap.TrayMenuLine(HotkeyAction.ClipboardTranslate, new AppSettings()));
    }

    [Fact]
    public void A_menu_row_without_a_hotkey_has_no_accelerator_column()
    {
        // 打开管理窗默认不设：行文里连 \t 都不出现，空列比没列诚实。
        Assert.Equal("管理历史…", KeyMap.TrayMenuLine(HotkeyAction.Library, new AppSettings()));
    }

    [Fact]
    public void The_reverse_input_row_is_named_and_carries_its_live_combination()
    {
        // 票 43：托盘菜单的动作行，名称"反向输入"，加速键列读现设置——改键即改菜单。
        Assert.Equal("反向输入", KeyMap.TrayLabel(HotkeyAction.ReverseInput));
        Assert.Equal("反向输入\tAlt+Q", KeyMap.TrayMenuLine(HotkeyAction.ReverseInput, new AppSettings()));
        Assert.Equal(
            "反向输入\tCtrl+Alt+R",
            KeyMap.TrayMenuLine(
                HotkeyAction.ReverseInput, new AppSettings { ReverseInputHotkey = "Ctrl+Alt+R" }));
        Assert.Equal(
            "反向输入",
            KeyMap.TrayMenuLine(HotkeyAction.ReverseInput, new AppSettings { ReverseInputHotkey = " " }));
    }

    [Fact]
    public void Tray_menu_covers_every_hotkey_action_with_a_plain_label()
    {
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var line = KeyMap.TrayMenuLine(action, new AppSettings());
            var label = line.Split('\t')[0];
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.Contains(label, line);
        }
    }

    [Fact]
    public void Chips_split_a_combination_into_keycaps()
    {
        Assert.Equal(new[] { "Ctrl", "Shift", "B" }, KeyMap.Chips("Ctrl+Shift+B"));
        Assert.Equal(new[] { "Alt", "S" }, KeyMap.Chips("Alt+S"));
    }

    [Fact]
    public void Chips_of_an_unset_combination_is_an_empty_list()
    {
        Assert.Empty(KeyMap.Chips(null));
        Assert.Empty(KeyMap.Chips("  "));
    }

    [Fact]
    public void Cheat_sheet_lines_snapshot_the_rendered_table()
    {
        // 速查区从这行文本渲染；快照钉住格式与内容，改表必改这里。管理窗
        // 段只在 Library 列写了不同键的行出现（同键回落不重复印）。
        Assert.Equal(
        [
            "搜索：窄条 Ctrl+F · 设置 Ctrl+F",
            "粘贴（主动作）：窄条 Enter（对选中的卡片）",
            "打开搜索结果（主动作）：设置 Enter（搜索有结果时）",
            "复制：窄条 C · 管理 C / Enter",
            "打开：窄条 O",
            "置顶：窄条 P",
            "收藏：窄条 S",
            "备注：窄条 N",
            "归组：窄条 G",
            "删除：窄条 D · 管理 D / Delete",
            "撤销：窄条 Z · 管理 Z / Ctrl+Z（删除后的片刻内）",
            "预览全文：窄条 按住 Space",
            "编号直达：窄条 1–9、0（列表前 10 行）",
            "切换类型：窄条 ←→",
            "只看收藏：窄条 Alt+S",
            "标签：窄条 Tab · 管理 T",
            "移动选择：管理 ↑ ↓",
            "连选：管理 Shift+↑ ↓",
            "全选：管理 Ctrl+A",
            "区域循环：管理 F6",
            "关闭窗口：管理 Ctrl+W",
            "切换页签：管理 Ctrl+Tab（剪贴板历史 / 翻译记录）",
            "显示键帽：窄条 按住 Ctrl · 设置 按住 Ctrl",
            "键位速查：设置 ? 或 F1 · 管理 F1 / ?",
            "切换设置页：设置 Ctrl+1–6",
            "分层退出：窄条 Esc",
        ],
            KeyMap.CheatSheetLines());
    }

    [Fact]
    public void For_surface_lists_only_rows_with_a_key_there()
    {
        var library = KeyMap.ForSurface(KeySurface.Library).Select(row => row.Id).ToList();

        // 管理窗速查有：共用动词（回落）、管理窗独有行、以及不同键的标签。
        Assert.Contains("copy", library);
        Assert.Contains("tag", library);
        Assert.Contains("select-all", library);
        Assert.Contains("close-window", library);

        // bar-only 的行不进管理窗速查；settings-only 的行也不进。
        Assert.DoesNotContain("numbered", library);
        Assert.DoesNotContain("favorite-filter", library);
        Assert.DoesNotContain("nav", library);
        Assert.DoesNotContain("paste", library);

        var settings = KeyMap.ForSurface(KeySurface.Settings).Select(row => row.Id).ToList();
        Assert.DoesNotContain("select-all", settings);
        Assert.Contains("search", settings);
    }

    [Fact]
    public void Effective_key_falls_back_to_bar_for_the_library_surface()
    {
        // 同键动词：管理窗生效键 = 窄条键（行里没写 Library 列）。
        Assert.Equal("P", KeyMap.EffectiveKey(KeyMap.Find("pin")!, KeySurface.Library));
        Assert.Equal("O", KeyMap.EffectiveKey(KeyMap.Find("open")!, KeySurface.Bar));

        // 不同键动词按列覆盖；管理窗独有行只有 Library 列。
        Assert.Equal("T", KeyMap.EffectiveKey(KeyMap.Find("tag")!, KeySurface.Library));
        Assert.Equal("Ctrl+A", KeyMap.EffectiveKey(KeyMap.Find("select-all")!, KeySurface.Library));
        Assert.Null(KeyMap.EffectiveKey(KeyMap.Find("select-all")!, KeySurface.Bar));
    }

    [Fact]
    public void Library_hint_letters_match_the_key_handler()
    {
        // 命令栏键帽（按住 Ctrl 原位换键帽）读这些行；键盘处理
        // （LibraryKeys.OnPreviewKeyDown）敲的也是这些字母——两处由
        // 这张表钉住一致，改键不会只改一边。
        foreach (var (id, letter) in new[]
                 {
                     ("copy", "C"), ("pin", "P"), ("favorite", "S"),
                     ("note", "N"), ("group", "G"), ("tag", "T"), ("delete", "D"),
                 })
        {
            Assert.Equal(letter, KeyMap.HintKey(id, KeySurface.Library));
        }
    }

    [Fact]
    public void Hint_keys_take_the_first_of_the_equivalent_forms()
    {
        // 等价键列表（"C / Enter"）只取第一个：键帽与菜单列装短形，
        // 完整并列属于速查行（EffectiveKey 的口径）。
        Assert.Equal("C", KeyMap.HintKey("copy", KeySurface.Library));
        Assert.Equal("Z", KeyMap.HintKey("undo", KeySurface.Library));
        Assert.Equal("F1", KeyMap.HintKey("cheatsheet", KeySurface.Library));
        Assert.Equal("C / Enter", KeyMap.EffectiveKey(KeyMap.Find("copy")!, KeySurface.Library));
        Assert.Null(KeyMap.HintKey("paste", KeySurface.Library));
    }

    [Fact]
    public void The_same_verb_keeps_one_key_across_windows()
    {
        // §5.2 一套动词跨窗同键：搜索在两窗都是 Ctrl+F。
        var search = KeyMap.Find("search")!;
        Assert.Equal(search.Bar, search.Settings);
    }

    /// <summary>票 27 / U-29：图标钮的自动化名由这张表渲染，格式「动作名（键）」。</summary>
    [Fact]
    public void Automation_names_compose_the_action_name_with_the_surface_key()
    {
        Assert.Equal("复制（C）", KeyMap.AutomationName("copy"));
        Assert.Equal("复制（C）", KeyMap.AutomationName("copy", KeySurface.Library));
        Assert.Equal("删除（D）", KeyMap.AutomationName("delete", KeySurface.Library));
        Assert.Equal("搜索（Ctrl+F）", KeyMap.AutomationName("search", KeySurface.Settings));
        Assert.Equal("分层退出（Esc）", KeyMap.AutomationName("escape"));
    }

    [Fact]
    public void The_library_surface_overrides_only_the_rows_whose_key_really_differs()
    {
        // 标签在窄条是 Tab、管理窗是 T（Tab 留给焦点导航）；复制/删除/撤销
        // 在管理窗有第二等价键（Enter/Delete/Ctrl+Z）写进 Library 列；其余
        // 动作两窗同键，行里不写 Library 列，回落 Bar——回落而不是复制，
        // 改键只改一处。
        Assert.Equal("标签（Tab）", KeyMap.AutomationName("tag"));
        Assert.Equal("标签（T）", KeyMap.AutomationName("tag", KeySurface.Library));

        foreach (var id in new[] { "pin", "favorite", "note", "group" })
        {
            Assert.Equal(
                KeyMap.AutomationName(id),
                KeyMap.AutomationName(id, KeySurface.Library));
        }
    }

    [Fact]
    public void A_row_without_a_key_names_the_action_alone()
    {
        // 粘贴的键是 Enter、名字自带括注，自动化名不再叠一层括号——
        // 没有键（窄条列空）的行只给动作名。这里用未设 Bar 键的行验证格式。
        Assert.Equal("打开搜索结果（主动作）", KeyMap.AutomationName("open-result", KeySurface.Bar));
    }

    [Fact]
    public void An_unknown_id_falls_back_to_itself_rather_than_empty()
    {
        // 空名字在 UIA 树里等于无名可念；宁可念 id，不让按钮变哑巴。
        Assert.Equal("no-such-action", KeyMap.AutomationName("no-such-action"));
    }
}
