using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 提示词模板库（票 42）：四个内置模板、按 id 解析、循环列表。引用失效一律回落
/// 到标准——这些都在数据层解析，界面只负责显示，所以全部是纯函数测试。
/// </summary>
public class PromptTemplateLibraryTests
{
    [Fact]
    public void The_four_built_in_templates_have_fixed_ids_and_names_in_cycle_order()
    {
        Assert.Equal(
            ["standard", "colloquial", "formal", "prompt-optimize"],
            PromptTemplates.BuiltIn.Select(template => template.Id).ToList());

        Assert.Equal(
            ["标准", "口语", "正式", "提示词优化"],
            PromptTemplates.BuiltIn.Select(template => template.Name).ToList());
    }

    [Fact]
    public void The_translation_templates_translate_and_the_optimizer_rewrites()
    {
        Assert.Equal(PromptTemplateKind.Translate, PromptTemplates.Standard.Kind);
        Assert.Equal(PromptTemplateKind.Translate, PromptTemplates.Colloquial.Kind);
        Assert.Equal(PromptTemplateKind.Translate, PromptTemplates.Formal.Kind);
        Assert.Equal(PromptTemplateKind.Rewrite, PromptTemplates.PromptOptimize.Kind);
    }

    [Fact]
    public void Built_in_translation_templates_carry_no_text_and_the_optimizer_carries_its_body()
    {
        // 翻译类的 system 由代码按规则与示例拼出，改写类的 Text 就是模板正文。
        Assert.Null(PromptTemplates.Standard.Text);
        Assert.Null(PromptTemplates.Colloquial.Text);
        Assert.Null(PromptTemplates.Formal.Text);
        Assert.False(string.IsNullOrWhiteSpace(PromptTemplates.PromptOptimize.Text));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("colloquial")]
    [InlineData("formal")]
    [InlineData("prompt-optimize")]
    public void The_four_ids_are_reserved_for_the_built_ins(string id)
    {
        Assert.True(PromptTemplates.IsReserved(id));
        Assert.True(PromptTemplates.BuiltIn.Single(template => template.Id == id).IsBuiltIn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("my-template")]
    [InlineData("3f2b8c1e-0000-4000-8000-000000000001")]
    public void Any_other_id_is_free_for_the_users_own_templates(string id)
        => Assert.False(PromptTemplates.IsReserved(id));

    [Theory]
    [InlineData("standard", 0.2)]
    [InlineData("colloquial", 0.3)]
    [InlineData("formal", 0.2)]
    [InlineData("prompt-optimize", 0.3)]
    public void Each_built_in_template_carries_its_own_temperature(string id, double expected)
        => Assert.Equal(expected, PromptTemplates.Resolve(new AppSettings(), id).Temperature);

    [Fact]
    public void A_users_own_rewrite_template_runs_at_the_rewrite_temperature()
        => Assert.Equal(0.3, new PromptTemplate("mine", "润色", PromptTemplateKind.Rewrite, "Polish it.").Temperature);

    [Fact]
    public void The_translation_templates_always_use_the_target_language()
    {
        Assert.True(PromptTemplates.Standard.UsesTarget);
        Assert.True(PromptTemplates.Colloquial.UsesTarget);
        Assert.True(PromptTemplates.Formal.UsesTarget);
    }

    [Fact]
    public void A_rewrite_template_uses_the_target_only_when_its_text_says_so()
    {
        Assert.True(PromptTemplates.PromptOptimize.UsesTarget);
        Assert.True(new PromptTemplate("a", "润色", PromptTemplateKind.Rewrite, "Polish it into {target}.").UsesTarget);
        Assert.False(new PromptTemplate("b", "文言文", PromptTemplateKind.Rewrite, "改写成文言文。").UsesTarget);
    }

    [Fact]
    public void A_request_without_a_template_is_a_standard_one()
    {
        var request = new TranslationRequest("hello", "Chinese");

        Assert.Equal(PromptTemplate.StandardId, request.Template.Id);
        Assert.Equal(0.2, request.Temperature);
    }

    // --- 按 id 解析：失效一律回落到标准 -------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-template")]
    public void A_missing_or_unknown_id_falls_back_to_standard(string? id)
        => Assert.Equal("standard", PromptTemplates.Resolve(new AppSettings(), id).Id);

    [Fact]
    public void A_known_id_resolves_to_its_template()
        => Assert.Equal("正式", PromptTemplates.Resolve(new AppSettings(), "formal").Name);

    [Fact]
    public void Fresh_settings_default_to_standard_and_cycle_through_all_four()
    {
        var settings = new AppSettings();

        Assert.Equal("standard", settings.DefaultPromptTemplateId);
        Assert.Equal(
            ["standard", "colloquial", "formal", "prompt-optimize"],
            settings.TemplateCycle.ToList());

        Assert.Equal("standard", PromptTemplates.ResolveDefault(settings).Id);
        Assert.Equal(
            ["standard", "colloquial", "formal", "prompt-optimize"],
            PromptTemplates.Cycle(settings).Select(template => template.Id).ToList());
    }

    [Fact]
    public void A_default_that_points_nowhere_resolves_to_standard()
    {
        var settings = new AppSettings { DefaultPromptTemplateId = "deleted-long-ago" };

        Assert.Equal("standard", PromptTemplates.ResolveDefault(settings).Id);
    }

    [Fact]
    public void A_chosen_default_resolves_to_that_template()
    {
        var settings = new AppSettings { DefaultPromptTemplateId = "colloquial" };

        Assert.Equal("colloquial", PromptTemplates.ResolveDefault(settings).Id);
    }

    [Fact]
    public void Unknown_ids_drop_out_of_the_cycle_and_repeats_collapse()
    {
        var settings = new AppSettings
        {
            TemplateCycle = ["formal", "ghost", "formal", "standard", "", "  "],
        };

        Assert.Equal(
            ["formal", "standard"],
            PromptTemplates.Cycle(settings).Select(template => template.Id).ToList());
    }

    [Fact]
    public void The_cycle_keeps_the_order_the_settings_give_it()
    {
        var settings = new AppSettings { TemplateCycle = ["prompt-optimize", "standard"] };

        Assert.Equal(
            ["prompt-optimize", "standard"],
            PromptTemplates.Cycle(settings).Select(template => template.Id).ToList());
    }

    [Fact]
    public void A_hand_broken_settings_file_still_resolves_instead_of_throwing()
    {
        // JSON 里写 null 会原样落进属性：读取端必须扛得住。
        var settings = new AppSettings { DefaultPromptTemplateId = null!, TemplateCycle = null! };

        Assert.Equal("standard", PromptTemplates.ResolveDefault(settings).Id);
        Assert.Empty(PromptTemplates.Cycle(settings));
    }

    // --- 循环 -------------------------------------------------------------------

    [Fact]
    public void Next_walks_the_cycle_in_order_and_wraps_round()
    {
        var cycle = PromptTemplates.Cycle(new AppSettings());

        Assert.Equal("colloquial", PromptTemplates.Next(PromptTemplates.Standard, cycle).Id);
        Assert.Equal("formal", PromptTemplates.Next(PromptTemplates.Colloquial, cycle).Id);
        Assert.Equal("prompt-optimize", PromptTemplates.Next(PromptTemplates.Formal, cycle).Id);
        Assert.Equal("standard", PromptTemplates.Next(PromptTemplates.PromptOptimize, cycle).Id);
    }

    [Fact]
    public void A_template_outside_the_cycle_steps_to_the_first_one_in_it()
    {
        var cycle = PromptTemplates.Cycle(new AppSettings { TemplateCycle = ["colloquial", "formal"] });

        Assert.Equal("colloquial", PromptTemplates.Next(PromptTemplates.Standard, cycle).Id);
    }

    [Fact]
    public void An_empty_cycle_has_nowhere_to_go()
    {
        var cycle = PromptTemplates.Cycle(new AppSettings { TemplateCycle = [] });

        Assert.Equal("formal", PromptTemplates.Next(PromptTemplates.Formal, cycle).Id);
    }

    // --- 面板的运行时模板与设置的关系 -----------------------------------------------

    [Fact]
    public void An_unrelated_settings_change_leaves_the_runtime_template_alone()
    {
        // 面板是复用实例，用户在面板里切到「正式」之后，别处改了无关设置
        // （比如译文语言）不该把它打回默认。
        var before = new AppSettings();
        var after = before with { TargetLanguage = "English" };

        Assert.Equal("formal", PromptTemplates.Reconcile(before, after, PromptTemplates.Formal).Id);
    }

    [Fact]
    public void Changing_the_default_overrides_the_runtime_template()
    {
        var before = new AppSettings();
        var after = before with { DefaultPromptTemplateId = "colloquial" };

        Assert.Equal("colloquial", PromptTemplates.Reconcile(before, after, PromptTemplates.Formal).Id);
    }

    [Fact]
    public void Changing_the_cycle_overrides_the_runtime_template_with_the_default()
    {
        var before = new AppSettings();
        var after = before with { TemplateCycle = ["standard", "formal"] };

        Assert.Equal("standard", PromptTemplates.Reconcile(before, after, PromptTemplates.Colloquial).Id);
    }

    [Fact]
    public void Re_ordering_the_same_cycle_counts_as_a_change()
    {
        var before = new AppSettings { TemplateCycle = ["standard", "formal"] };
        var after = before with { TemplateCycle = ["formal", "standard"] };

        Assert.Equal("standard", PromptTemplates.Reconcile(before, after, PromptTemplates.Formal).Id);
    }

    // --- 设置：落盘与旧文件 -------------------------------------------------------

    [Fact]
    public void The_default_and_the_cycle_survive_a_round_trip()
    {
        var original = new AppSettings
        {
            DefaultPromptTemplateId = "colloquial",
            TemplateCycle = ["colloquial", "prompt-optimize"],
        };

        Assert.True(AppSettings.TryParse(original.ToBackupJson(includeKey: false), out var loaded));

        Assert.Equal("colloquial", loaded.DefaultPromptTemplateId);
        Assert.Equal(["colloquial", "prompt-optimize"], loaded.TemplateCycle.ToList());
    }

    [Fact]
    public void A_settings_file_from_before_templates_gets_the_defaults()
    {
        Assert.True(AppSettings.TryParse("""{"TargetLanguage":"Japanese"}""", out var loaded));

        Assert.Equal("standard", loaded.DefaultPromptTemplateId);
        Assert.Equal(
            ["standard", "colloquial", "formal", "prompt-optimize"],
            loaded.TemplateCycle.ToList());
    }

    // --- 只在自备密钥的大模型下生效 -----------------------------------------------

    [Fact]
    public void Templates_apply_to_an_own_key_backend()
        => Assert.True(new AppSettings { TranslationBackend = TranslationBackendKind.OwnKey }.PromptTemplatesApply);

    [Fact]
    public void Templates_apply_when_the_relay_is_not_live_because_it_falls_back_to_the_own_key_backend()
    {
        // 公共通道上线条件未满足时，选 Relay 的用户实际走的是自备密钥后端。
        Assert.False(RelayChannel.Available);
        Assert.True(new AppSettings { TranslationBackend = TranslationBackendKind.Relay }.PromptTemplatesApply);
    }

    [Fact]
    public void The_verdict_follows_the_backend_that_is_actually_built()
    {
        // 与 BuildTranslationBackend 走同一套分支：建出来的是自备密钥大模型就是
        // 真，否则（日后的免费引擎、已上线的公共通道）是假。
        foreach (var kind in Enum.GetValues<TranslationBackendKind>())
        {
            var settings = new AppSettings { TranslationBackend = kind };

            Assert.Equal(
                settings.BuildTranslationBackend() is OpenAiCompatibleBackend,
                settings.PromptTemplatesApply);
        }
    }

    [Fact]
    public void The_free_engine_takes_no_templates()
    {
        // 票 41 × 42 的接缝写明一条：网页翻译没有 prompt 这一层——免费引擎下面板的
        // 模板按钮隐藏、请求按标准模板构建，回声换向照常工作。上面的循环也覆盖它，
        // 但这条退化时要一眼看出是哪一家。
        var settings = new AppSettings { TranslationBackend = TranslationBackendKind.Free };

        Assert.IsType<FreeEngineBackend>(settings.BuildTranslationBackend());
        Assert.False(settings.PromptTemplatesApply);
    }

    [Fact]
    public void The_verdict_is_derived_not_stored()
    {
        var json = new AppSettings().ToBackupJson(includeKey: false);

        Assert.DoesNotContain("PromptTemplatesApply", json);
    }
}

/// <summary>
/// 面板头部与布局据此取舍：纯数据，WPF 只负责照着显示。没有 WPF 单测，所以把
/// "什么时候显示按钮、什么时候藏方向"这些规则放在 Core 里钉住。
/// </summary>
public class PanelTemplateStateTests
{
    private static readonly PromptTemplate NoTarget =
        new("mine", "文言文", PromptTemplateKind.Rewrite, "改写成文言文。");

    private static readonly PromptTemplate WithTarget =
        new("mine2", "润色", PromptTemplateKind.Rewrite, "Polish it. Write in {target}.");

    [Fact]
    public void A_translation_template_shows_the_button_the_direction_and_the_mode_segment()
    {
        var state = PanelTemplateState.For(PromptTemplates.Colloquial, templatesApply: true, wordMode: false);

        Assert.Equal("colloquial", state.Template.Id);
        Assert.True(state.ButtonVisible);
        Assert.True(state.DirectionVisible);
        Assert.True(state.ModeSegmentVisible);
    }

    [Fact]
    public void A_template_without_the_target_hides_the_direction_so_only_its_name_shows()
    {
        var state = PanelTemplateState.For(NoTarget, templatesApply: true, wordMode: false);

        Assert.True(state.ButtonVisible);
        Assert.False(state.DirectionVisible);
    }

    [Fact]
    public void A_template_with_the_target_keeps_the_direction()
    {
        var state = PanelTemplateState.For(WithTarget, templatesApply: true, wordMode: false);

        Assert.True(state.DirectionVisible);
    }

    public static TheoryData<PromptTemplate> RewriteTemplates => new()
    {
        PromptTemplates.PromptOptimize,
        NoTarget,
        WithTarget,
    };

    [Theory]
    [MemberData(nameof(RewriteTemplates))]
    public void A_rewrite_template_hides_the_whole_sentence_mode_segment(PromptTemplate template)
    {
        // 逐句对照是给译文用的：改写结果和原文的句子对不上，固定为整段。
        var state = PanelTemplateState.For(template, templatesApply: true, wordMode: false);

        Assert.False(state.ModeSegmentVisible);
    }

    [Fact]
    public void Back_on_a_translation_template_the_mode_segment_returns()
    {
        var rewrite = PanelTemplateState.For(PromptTemplates.PromptOptimize, true, false);
        var back = PanelTemplateState.For(PromptTemplates.Formal, true, false);

        Assert.False(rewrite.ModeSegmentVisible);
        Assert.True(back.ModeSegmentVisible);
    }

    [Fact]
    public void A_single_word_is_always_translated_by_the_standard_template_and_has_no_button()
    {
        // 单词态（词典卡）不受模板影响：一律按标准翻译；按钮在单词态下隐藏，
        // 原有的方向标签照旧。模式分段在单词态本来就是藏着的。
        var state = PanelTemplateState.For(PromptTemplates.PromptOptimize, templatesApply: true, wordMode: true);

        Assert.Equal("standard", state.Template.Id);
        Assert.False(state.ButtonVisible);
        Assert.True(state.DirectionVisible);
        Assert.False(state.ModeSegmentVisible);
    }

    [Fact]
    public void Where_templates_do_not_apply_the_button_hides_and_requests_are_standard()
    {
        // 免费引擎没有 prompt 这一层：按钮隐藏，请求一律按标准模板构建，
        // 这样免费引擎下回声换向照常工作。
        var state = PanelTemplateState.For(PromptTemplates.PromptOptimize, templatesApply: false, wordMode: false);

        Assert.Equal("standard", state.Template.Id);
        Assert.False(state.ButtonVisible);
        Assert.True(state.DirectionVisible);
        Assert.True(state.ModeSegmentVisible);
    }

    [Fact]
    public void The_accessible_name_names_the_runtime_template_in_full_and_says_it_switches()
    {
        var state = PanelTemplateState.For(PromptTemplates.Colloquial, true, false);

        Assert.Equal("提示词模板：口语，点击切换", state.AccessibleName);
    }

    [Fact]
    public void The_accessible_name_keeps_the_full_name_however_long()
    {
        // 面板按钮按显示宽度截断，tooltip 与 a11y 名用全名。
        var lengthy = new PromptTemplate("long", "一个名字特别特别长的模板", PromptTemplateKind.Rewrite, "x");

        Assert.Equal(
            "提示词模板：一个名字特别特别长的模板，点击切换",
            PanelTemplateState.For(lengthy, true, false).AccessibleName);
    }

    [Fact]
    public void The_accessible_name_follows_the_state_as_the_template_changes()
    {
        var cycle = PromptTemplates.Cycle(new AppSettings());
        var first = PanelTemplateState.For(PromptTemplates.Standard, true, false);
        var second = PanelTemplateState.For(PromptTemplates.Next(first.Template, cycle), true, false);

        Assert.NotEqual(first.AccessibleName, second.AccessibleName);
        Assert.Equal("提示词模板：口语，点击切换", second.AccessibleName);
    }
}

/// <summary>
/// 设置·翻译下的「提示词模板」一节：Custom 控件，关键词让用户用自己的说法也找得到
/// （提示词、prompt、模板、口语、正式、优化、vibe coding）。
/// </summary>
public class PromptTemplateSchemaTests
{
    private static SettingsItem Item()
        => SettingsSchema.Tree.SelectMany(page => page.Sections)
            .SelectMany(section => section.Items)
            .Single(item => item.Id == "translate.templates");

    [Fact]
    public void The_templates_item_is_a_custom_control_on_the_translate_page()
    {
        var item = Item();

        Assert.Equal(SettingsControl.Custom, item.Control);
        Assert.Equal("translate", SettingsSchema.FindPageOf(item.Id)!.Id);
        Assert.Equal("提示词模板", item.Label);
        Assert.False(string.IsNullOrWhiteSpace(item.Hint));
        Assert.False(string.IsNullOrWhiteSpace(item.Icon));
    }

    [Fact]
    public void The_templates_item_gets_the_whole_row_because_its_editor_is_tall()
        => Assert.True(Item().FullBleed);

    [Fact]
    public void The_templates_live_in_a_section_of_their_own_under_translation()
    {
        var section = SettingsSchema.Tree.Single(page => page.Id == "translate")
            .Sections.Single(s => s.Items.Any(item => item.Id == "translate.templates"));

        Assert.Equal("提示词模板", section.Title);
    }

    [Theory]
    [InlineData("提示词")]
    [InlineData("prompt")]
    [InlineData("模板")]
    [InlineData("口语")]
    [InlineData("正式")]
    [InlineData("优化")]
    [InlineData("vibe coding")]
    public void The_templates_are_found_by_the_words_users_reach_for(string query)
        => Assert.Contains(
            SettingsSearch.Find(query),
            hit => hit.Item.Id == "translate.templates");
}
