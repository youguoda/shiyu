using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class HotkeyPlanTests
{
    private static AppSettings Settings(
        string capture = "Ctrl+Shift+Z",
        string quickBar = "Ctrl+Shift+V",
        string bar = "Ctrl+Shift+B",
        string clipboard = "Ctrl+Shift+X",
        string library = "",
        string reverse = "Alt+Q")
        => new()
        {
            CaptureHotkey = capture,
            QuickBarHotkey = quickBar,
            BarHotkey = bar,
            ClipboardTranslateHotkey = clipboard,
            LibraryHotkey = library,
            ReverseInputHotkey = reverse,
        };

    /// <summary>注册顺序：与 <see cref="HotkeyPlan.Build"/> 里的固定顺序一致。</summary>
    private static readonly HotkeyAction[] RegistrationOrder =
    [
        HotkeyAction.CaptureSelection,
        HotkeyAction.QuickBar,
        HotkeyAction.Bar,
        HotkeyAction.ClipboardTranslate,
        HotkeyAction.Library,
        HotkeyAction.ReverseInput,
    ];

    // --- the good path ----------------------------------------------------------

    [Fact]
    public void The_default_hotkeys_build_five_bindings_and_no_problems()
    {
        // 管理窗默认不设（空串 = 不注册），所以六个槽位出厂只有五个键：
        // 反向输入（票 43）是新增的那一个，默认 Alt+Q。
        var (bindings, problems) = HotkeyPlan.Build(Settings());

        Assert.Empty(problems);
        // Registration order, so a collision elsewhere would still resolve the
        // way the App has always resolved it.
        Assert.Equal(
            [
                HotkeyAction.CaptureSelection,
                HotkeyAction.QuickBar,
                HotkeyAction.Bar,
                HotkeyAction.ClipboardTranslate,
                HotkeyAction.ReverseInput,
            ],
            bindings.Select(b => b.Action));
        Assert.Equal("Ctrl+Shift+Z", bindings[0].Spec.ToString());
    }

    [Fact]
    public void Every_hotkey_action_has_a_slot_and_a_name()
    {
        // 新增 HotkeyAction 而忘了接进注册方案，是这条测试要抓的漂移。
        var all = Enum.GetValues<HotkeyAction>();
        Assert.Equal(RegistrationOrder.OrderBy(action => action), all.OrderBy(action => action));

        foreach (var action in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(HotkeyPlan.ActionNames[action]));
        }
    }

    [Fact]
    public void All_six_slots_filled_with_distinct_keys_build_six_bindings()
    {
        var (bindings, problems) = HotkeyPlan.Build(Settings(library: "Ctrl+Shift+L"));

        Assert.Empty(problems);
        Assert.Equal(RegistrationOrder, bindings.Select(b => b.Action));
    }

    // --- 反向输入（票 43）----------------------------------------------------------

    [Fact]
    public void The_reverse_input_hotkey_defaults_to_Alt_Q_and_is_named_in_plain_words()
    {
        Assert.Equal("Alt+Q", new AppSettings().ReverseInputHotkey);
        Assert.Equal("反向输入", HotkeyPlan.ActionNames[HotkeyAction.ReverseInput]);

        var binding = Assert.Single(
            HotkeyPlan.Build(new AppSettings()).Bindings, b => b.Action == HotkeyAction.ReverseInput);
        Assert.Equal(HotkeyModifier.Alt, binding.Spec.Modifiers);
        Assert.Equal('Q', binding.Spec.Key);
    }

    [Fact]
    public void An_empty_reverse_input_hotkey_is_a_deliberate_unset_not_a_problem()
    {
        var (bindings, problems) = HotkeyPlan.Build(Settings(reverse: ""));

        Assert.Empty(problems);
        Assert.DoesNotContain(bindings, b => b.Action == HotkeyAction.ReverseInput);
    }

    [Fact]
    public void A_bare_reverse_input_key_is_rejected_in_plain_words()
    {
        var (bindings, problems) = HotkeyPlan.Build(Settings(reverse: "Q"));

        var problem = Assert.Single(problems);
        Assert.Contains("反向输入", problem);
        Assert.Contains("至少带一个修饰键", problem);
        Assert.DoesNotContain(bindings, b => b.Action == HotkeyAction.ReverseInput);
    }

    [Fact]
    public void Setting_another_hotkey_to_Alt_Q_collides_with_the_reverse_input_and_names_both()
    {
        var (bindings, problems) = HotkeyPlan.Build(Settings(bar: "Alt+Q"));

        var problem = Assert.Single(problems);
        Assert.Contains("窄条", problem);
        Assert.Contains("反向输入", problem);
        Assert.Contains("Alt+Q", problem);

        // 先注册的赢：窄条在前，反向输入让出。
        Assert.Contains(bindings, b => b.Action == HotkeyAction.Bar);
        Assert.DoesNotContain(bindings, b => b.Action == HotkeyAction.ReverseInput);
    }

    // --- collisions: every pair named -------------------------------------------

    public static TheoryData<HotkeyAction, HotkeyAction> EveryPair()
    {
        var data = new TheoryData<HotkeyAction, HotkeyAction>();
        for (var i = 0; i < RegistrationOrder.Length; i++)
        {
            for (var j = i + 1; j < RegistrationOrder.Length; j++)
            {
                data.Add(RegistrationOrder[i], RegistrationOrder[j]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void A_collision_names_both_actions(HotkeyAction a, HotkeyAction b)
    {
        // Every action gets its own distinct key except the colliding pair,
        // which shares Ctrl+Alt+K.
        var keyed = new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.CaptureSelection] = "Ctrl+Alt+1",
            [HotkeyAction.QuickBar] = "Ctrl+Alt+2",
            [HotkeyAction.Bar] = "Ctrl+Alt+3",
            [HotkeyAction.ClipboardTranslate] = "Ctrl+Alt+4",
            [HotkeyAction.Library] = "Ctrl+Alt+5",
            [HotkeyAction.ReverseInput] = "Ctrl+Alt+6",
            [a] = "Ctrl+Alt+K",
            [b] = "Ctrl+Alt+K",
        };

        var (bindings, problems) = HotkeyPlan.Build(Settings(
            keyed[HotkeyAction.CaptureSelection],
            keyed[HotkeyAction.QuickBar],
            keyed[HotkeyAction.Bar],
            keyed[HotkeyAction.ClipboardTranslate],
            keyed[HotkeyAction.Library],
            keyed[HotkeyAction.ReverseInput]));

        var problem = Assert.Single(problems);
        Assert.Contains(HotkeyPlan.ActionNames[a], problem);
        Assert.Contains(HotkeyPlan.ActionNames[b], problem);
        Assert.Contains("Ctrl+Alt+K", problem);

        // First registered wins the key; the loser is left out (registered
        // elsewhere it would only fail at RegisterHotKey and get reported a
        // second time, as a misleading "taken by other software").
        var loser = Array.IndexOf(RegistrationOrder, a) > Array.IndexOf(RegistrationOrder, b) ? a : b;
        Assert.Equal(RegistrationOrder.Length - 1, bindings.Count);
        Assert.DoesNotContain(bindings, x => x.Action == loser);
    }

    [Fact]
    public void Three_keys_sharing_one_combination_report_three_pairwise_problems_and_keep_one()
    {
        var (bindings, problems) = HotkeyPlan.Build(Settings(
            capture: "Ctrl+Alt+K", quickBar: "Ctrl+Alt+K", bar: "Ctrl+Alt+K"));

        // C(3,2) pairs, one problem each — every pair is named.
        Assert.Equal(3, problems.Count);
        Assert.All(problems, p => Assert.Contains("Ctrl+Alt+K", p));

        // First registered keeps the key; the other two are left out rather
        // than registered to fail. The untouched fourth key and the reverse
        // input survive.
        var survivor = Assert.Single(bindings, b => b.Spec.ToString() == "Ctrl+Alt+K");
        Assert.Equal(HotkeyAction.CaptureSelection, survivor.Action);
        Assert.Equal(3, bindings.Count);
    }

    // --- the onboarding regression ------------------------------------------------

    [Fact]
    public void Setting_the_bar_hotkey_to_the_quick_bar_default_is_reported_as_a_collision()
    {
        // The guide edits three hotkeys and leaves QuickBar at its default
        // (Ctrl+Shift+V). Its old validation only compared the three edited
        // keys, so this sailed through and the App's registration then failed
        // with a false "taken by other software". The plan checks every key.
        var (bindings, problems) = HotkeyPlan.Build(Settings(bar: "Ctrl+Shift+V"));

        var problem = Assert.Single(problems);
        Assert.Contains(HotkeyPlan.ActionNames[HotkeyAction.QuickBar], problem);
        Assert.Contains(HotkeyPlan.ActionNames[HotkeyAction.Bar], problem);

        // First registered wins, as before: the quick bar keeps the key.
        Assert.DoesNotContain(bindings, b => b.Action == HotkeyAction.Bar);
        Assert.Contains(bindings, b => b.Action == HotkeyAction.QuickBar);
    }

    // --- unreadable input ----------------------------------------------------------

    [Fact]
    public void A_bare_key_without_modifiers_is_rejected_in_plain_words()
    {
        var (bindings, problems) = HotkeyPlan.Build(Settings(capture: "Z"));

        Assert.DoesNotContain(bindings, b => b.Action == HotkeyAction.CaptureSelection);
        var problem = Assert.Single(problems);
        Assert.Contains(HotkeyPlan.ActionNames[HotkeyAction.CaptureSelection], problem);
        Assert.Contains("至少带一个修饰键", problem);
    }

    [Fact]
    public void Unreadable_text_costs_one_hotkey_not_the_set()
    {
        var (bindings, problems) = HotkeyPlan.Build(Settings(clipboard: "Ctrl++"));

        var problem = Assert.Single(problems);
        Assert.Contains(HotkeyPlan.ActionNames[HotkeyAction.ClipboardTranslate], problem);

        // 另外三个有键的动作与反向输入照常注册。
        Assert.Equal(4, bindings.Count);
        Assert.DoesNotContain(bindings, b => b.Action == HotkeyAction.ClipboardTranslate);
    }
}
