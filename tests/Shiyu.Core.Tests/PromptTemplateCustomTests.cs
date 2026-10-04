using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 自建模板的存储（票 42 阶段二）：放在 <c>AppSettings.PromptTemplates</c>，形状同
/// <c>ExclusionRules</c>；随设置一起备份与恢复。
/// </summary>
// AppSettings.SecretProtector 是环境状态：写盘读盘的测试不与别的设置 IO 并行。
[Collection("settings-io")]
public class CustomPromptTemplateStorageTests
{
    private static StoredPromptTemplate Polish { get; }
        = new("3f2b8c1e-0000-4000-8000-000000000001", "润色", "Polish the text. Keep the tone.");

    private static StoredPromptTemplate Classical { get; }
        = new("3f2b8c1e-0000-4000-8000-000000000002", "文言文", "改写成文言文，不要解释。");

    private sealed class TempFile : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "shiyu-templates", Guid.NewGuid().ToString("N"));

        public TempFile()
        {
            Directory.CreateDirectory(_directory);
            Path_ = Path.Combine(_directory, "settings.json");
        }

        public string Path_ { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Fresh_settings_have_no_templates_of_their_own()
        => Assert.Empty(new AppSettings().PromptTemplates);

    [Fact]
    public void Templates_survive_a_save_and_a_load()
    {
        using var file = new TempFile();
        var original = new AppSettings { PromptTemplates = [Polish, Classical] };

        original.Save(file.Path_);
        var loaded = AppSettings.Load(file.Path_);

        Assert.Equal([Polish, Classical], loaded.PromptTemplates.ToList());
    }

    [Fact]
    public void Templates_travel_with_a_backup_and_come_back_on_restore()
    {
        // 备份与恢复的往返：自建模板、默认模板与循环一起走。
        var original = new AppSettings
        {
            PromptTemplates = [Polish, Classical],
            DefaultPromptTemplateId = Polish.Id,
            TemplateCycle = ["standard", Polish.Id],
        };

        var json = original.ToBackupJson(includeKey: false);
        Assert.True(AppSettings.TryParse(json, out var restored));

        Assert.Equal([Polish, Classical], restored.PromptTemplates.ToList());
        Assert.Equal(Polish.Id, restored.DefaultPromptTemplateId);
        Assert.Equal(["standard", Polish.Id], restored.TemplateCycle.ToList());

        // 恢复出来的设置里，自建模板照常解析。
        Assert.Equal("润色", PromptTemplates.ResolveDefault(restored).Name);
    }

    [Fact]
    public void A_settings_file_from_before_custom_templates_loads_with_none()
    {
        Assert.True(AppSettings.TryParse("""{"TargetLanguage":"Japanese"}""", out var loaded));

        Assert.Empty(loaded.PromptTemplates);
    }

    [Fact]
    public void A_hand_broken_list_leaves_only_the_templates_that_can_be_used()
    {
        // 文件被手改坏：null 条目、缺字段、占了保留 id、id 重复。能用的留着，其余
        // 一律当作不存在——不抛、不整体丢弃。
        var settings = new AppSettings
        {
            PromptTemplates =
            [
                Polish,
                null!,
                new StoredPromptTemplate(null!, "没有 id", "text"),
                new StoredPromptTemplate("no-name", "  ", "text"),
                new StoredPromptTemplate("no-prompt", "没有正文", "   "),
                new StoredPromptTemplate("standard", "冒充标准", "text"),
                new StoredPromptTemplate(Polish.Id, "重复的 id", "text"),
                Classical,
            ],
        };

        Assert.Equal(
            ["润色", "文言文"],
            PromptTemplates.Custom(settings).Select(template => template.Name).ToList());
    }

    [Fact]
    public void A_null_list_does_not_throw()
    {
        var settings = new AppSettings { PromptTemplates = null! };

        Assert.Equal(PromptTemplates.BuiltIn.Count, PromptTemplates.All(settings).Count);
    }
}

/// <summary>自建模板在库里的样子：内置在前、只读；自建在后，是改写类。</summary>
public class CustomPromptTemplateLibraryTests
{
    private static readonly StoredPromptTemplate Polish =
        new("polish-id", "润色", "Polish the text into {target}.");

    private static readonly StoredPromptTemplate Classical =
        new("classical-id", "文言文", "改写成文言文。");

    private static AppSettings Settings() => new() { PromptTemplates = [Polish, Classical] };

    private static List<string> Ids(IEnumerable<PromptTemplate> templates)
        => templates.Select(template => template.Id).ToList();

    [Fact]
    public void The_built_ins_come_first_then_the_users_own_in_order()
        => Assert.Equal(
            ["standard", "colloquial", "formal", "prompt-optimize", "polish-id", "classical-id"],
            Ids(PromptTemplates.All(Settings())));

    [Fact]
    public void A_users_template_is_a_rewrite_template_whose_text_is_its_prompt()
    {
        var template = PromptTemplates.Resolve(Settings(), "polish-id");

        Assert.Equal("润色", template.Name);
        Assert.Equal(PromptTemplateKind.Rewrite, template.Kind);
        Assert.Equal("Polish the text into {target}.", template.Text);
        Assert.False(template.IsBuiltIn);
        Assert.True(template.UsesTarget);
        Assert.Equal(0.3, template.Temperature);
    }

    [Fact]
    public void A_users_template_that_does_not_ask_for_the_target_says_so()
        => Assert.False(PromptTemplates.Resolve(Settings(), "classical-id").UsesTarget);

    [Fact]
    public void A_users_template_can_be_the_default()
    {
        var settings = Settings() with { DefaultPromptTemplateId = "polish-id" };

        Assert.Equal("polish-id", PromptTemplates.ResolveDefault(settings).Id);
    }

    [Fact]
    public void A_users_template_in_the_cycle_shows_up_in_the_panels_cycle_after_the_built_ins()
    {
        var settings = Settings() with
        {
            TemplateCycle = ["standard", "colloquial", "formal", "prompt-optimize", "polish-id"],
        };

        Assert.Equal(
            ["standard", "colloquial", "formal", "prompt-optimize", "polish-id"],
            Ids(PromptTemplates.Cycle(settings)));
    }

    [Fact]
    public void A_users_template_left_out_of_the_cycle_stays_out()
        => Assert.DoesNotContain("polish-id", Ids(PromptTemplates.Cycle(Settings())));

    [Fact]
    public void A_cycle_entry_whose_template_was_deleted_is_filtered_out_when_read()
    {
        // 模板被删、循环里还留着它的 id（文件被手改，或别处删的）：读取端滤掉。
        var settings = new AppSettings
        {
            TemplateCycle = ["standard", "polish-id"],
            PromptTemplates = [],
        };

        Assert.Equal(["standard"], Ids(PromptTemplates.Cycle(settings)));
    }

    [Fact]
    public void A_default_that_points_at_a_deleted_template_falls_back_to_standard()
    {
        var settings = new AppSettings { DefaultPromptTemplateId = "polish-id", PromptTemplates = [] };

        Assert.Equal("standard", PromptTemplates.ResolveDefault(settings).Id);
    }

    [Fact]
    public void The_runtime_template_follows_an_edit_to_the_template_it_is_on()
    {
        // 面板里切到了自建的「润色」，随后用户在设置里改了它的正文：下一次翻译
        // 就用新正文，不必先切走再切回来。
        var before = Settings();
        var edited = before with
        {
            PromptTemplates = [Polish with { Prompt = "Polish harder." }, Classical],
        };

        var runtime = PromptTemplates.Reconcile(before, edited, PromptTemplates.Resolve(before, "polish-id"));

        Assert.Equal("polish-id", runtime.Id);
        Assert.Equal("Polish harder.", runtime.Text);
    }

    [Fact]
    public void The_runtime_template_falls_back_to_standard_when_its_template_is_deleted()
    {
        var before = Settings();
        var after = before with { PromptTemplates = [Classical] };

        var runtime = PromptTemplates.Reconcile(before, after, PromptTemplates.Resolve(before, "polish-id"));

        Assert.Equal("standard", runtime.Id);
    }
}

/// <summary>
/// 校验（Core）：名字非空且不重名（不区分大小写，内置模板也算在内）；提示词非空、不超过
/// 4000 字；保留 id 不可占用。
/// </summary>
public class CustomPromptTemplateValidationTests
{
    private static readonly StoredPromptTemplate Polish = new("polish-id", "Polish", "Polish the text.");

    private static AppSettings Settings() => new() { PromptTemplates = [Polish] };

    private static string? Check(string name, string prompt, string id = "new-id", bool isNew = true, AppSettings? settings = null)
        => PromptTemplates.Validate(settings ?? Settings(), new StoredPromptTemplate(id, name, prompt), isNew);

    [Fact]
    public void A_good_template_passes()
        => Assert.Null(Check("文言文", "改写成文言文。"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void The_name_is_required(string name)
        => Assert.Contains("名字不能为空", Check(name, "text"));

    [Theory]
    [InlineData("口语")]
    [InlineData("标准")]
    [InlineData("提示词优化")]
    [InlineData(" 正式 ")]
    public void A_built_in_templates_name_is_taken(string name)
        => Assert.Contains("已经有叫", Check(name, "text"));

    [Theory]
    [InlineData("Polish")]
    [InlineData("polish")]
    [InlineData("POLISH")]
    public void Names_are_compared_without_regard_to_case(string name)
        => Assert.Contains("已经有叫", Check(name, "text"));

    [Fact]
    public void A_template_may_keep_its_own_name_when_it_is_edited()
        => Assert.Null(Check("Polish", "A new body.", id: "polish-id", isNew: false));

    [Fact]
    public void An_edit_cannot_take_another_templates_name()
    {
        var settings = new AppSettings
        {
            PromptTemplates = [Polish, new StoredPromptTemplate("other-id", "Other", "x")],
        };

        Assert.Contains("已经有叫", Check("other", "x", id: "polish-id", isNew: false, settings: settings));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public void The_prompt_is_required(string prompt)
        => Assert.Contains("提示词不能为空", Check("文言文", prompt));

    [Fact]
    public void The_prompt_may_run_to_four_thousand_characters_but_not_one_more()
    {
        Assert.Equal(4000, PromptTemplates.MaxPromptLength);
        Assert.Null(Check("文言文", new string('字', 4000)));
        Assert.Contains("4000", Check("文言文", new string('字', 4001)));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("colloquial")]
    [InlineData("formal")]
    [InlineData("prompt-optimize")]
    public void A_reserved_id_cannot_be_taken(string id)
        => Assert.Contains("保留", Check("文言文", "text", id: id));

    [Fact]
    public void An_id_is_required()
        => Assert.Contains("标识", Check("文言文", "text", id: "  "));

    [Fact]
    public void A_new_template_cannot_reuse_an_existing_id()
        => Assert.Contains("标识", Check("文言文", "text", id: "polish-id", isNew: true));

    [Fact]
    public void An_edit_of_a_template_that_no_longer_exists_is_refused()
        => Assert.Contains("不存在", Check("文言文", "text", id: "gone", isNew: false));

    [Fact]
    public void The_id_is_checked_before_the_words()
        => Assert.Contains("保留", Check("", "", id: "standard"));
}

/// <summary>新建、编辑、删除、复制为自建——写设置的逻辑放在 Core 里，界面只负责调用。</summary>
public class CustomPromptTemplateEditTests
{
    private static readonly StoredPromptTemplate Polish = new("polish-id", "润色", "Polish the text into {target}.");

    private static List<string> CycleIds(AppSettings settings)
        => PromptTemplates.Cycle(settings).Select(template => template.Id).ToList();

    // --- 新建 ---------------------------------------------------------------------

    [Fact]
    public void A_new_template_is_added_after_the_existing_ones()
    {
        var edited = PromptTemplates.AddCustom(
            new AppSettings { PromptTemplates = [Polish] },
            new StoredPromptTemplate("classical-id", "文言文", "改写成文言文。"),
            inCycle: false);

        Assert.Equal(["polish-id", "classical-id"], edited.PromptTemplates.Select(t => t.Id).ToList());
    }

    [Fact]
    public void A_new_template_ticked_into_the_cycle_shows_up_in_the_panel()
    {
        var edited = PromptTemplates.AddCustom(new AppSettings(), Polish, inCycle: true);

        // 自建模板勾选进循环后出现在面板里：内置在前，自建在后。
        Assert.Equal(
            ["standard", "colloquial", "formal", "prompt-optimize", "polish-id"],
            CycleIds(edited));
    }

    [Fact]
    public void A_new_template_left_unticked_is_not_in_the_cycle()
    {
        var edited = PromptTemplates.AddCustom(new AppSettings(), Polish, inCycle: false);

        Assert.DoesNotContain("polish-id", CycleIds(edited));
        Assert.Contains("polish-id", PromptTemplates.All(edited).Select(t => t.Id));
    }

    [Fact]
    public void An_invalid_template_is_not_added()
    {
        var before = new AppSettings();

        var edited = PromptTemplates.AddCustom(before, new StoredPromptTemplate("x", "口语", "text"), true);

        Assert.Same(before, edited);
    }

    // --- 编辑 ---------------------------------------------------------------------

    [Fact]
    public void An_edit_replaces_the_name_and_the_prompt_in_place()
    {
        var settings = new AppSettings { PromptTemplates = [Polish, new StoredPromptTemplate("b", "乙", "x")] };

        var edited = PromptTemplates.UpdateCustom(
            settings, new StoredPromptTemplate("polish-id", "润饰", "Polish it."), inCycle: false);

        Assert.Equal(
            [("polish-id", "润饰", "Polish it."), ("b", "乙", "x")],
            edited.PromptTemplates.Select(t => (t.Id, t.Name, t.Prompt)).ToList());
    }

    [Fact]
    public void An_edit_can_put_the_template_into_the_cycle_or_take_it_out()
    {
        var added = PromptTemplates.AddCustom(new AppSettings(), Polish, inCycle: true);

        var out_ = PromptTemplates.UpdateCustom(added, Polish, inCycle: false);
        Assert.DoesNotContain("polish-id", CycleIds(out_));

        var back = PromptTemplates.UpdateCustom(out_, Polish, inCycle: true);
        Assert.Contains("polish-id", CycleIds(back));
    }

    [Fact]
    public void An_edit_keeps_the_template_the_default_when_it_was()
    {
        var settings = new AppSettings
        {
            PromptTemplates = [Polish],
            DefaultPromptTemplateId = "polish-id",
        };

        var edited = PromptTemplates.UpdateCustom(settings, Polish with { Name = "润饰" }, inCycle: false);

        Assert.Equal("polish-id", PromptTemplates.ResolveDefault(edited).Id);
        Assert.Equal("润饰", PromptTemplates.ResolveDefault(edited).Name);
    }

    [Fact]
    public void An_edit_of_a_missing_or_built_in_template_changes_nothing()
    {
        var before = new AppSettings { PromptTemplates = [Polish] };

        Assert.Same(before, PromptTemplates.UpdateCustom(before, new StoredPromptTemplate("gone", "x", "y"), true));
        Assert.Same(before, PromptTemplates.UpdateCustom(before, new StoredPromptTemplate("standard", "x", "y"), true));
    }

    // --- 删除 ---------------------------------------------------------------------

    [Fact]
    public void A_deleted_template_is_gone_and_leaves_the_cycle()
    {
        var added = PromptTemplates.AddCustom(new AppSettings(), Polish, inCycle: true);

        var deleted = PromptTemplates.DeleteCustom(added, "polish-id");

        // 删除后从循环里消失——不只是读取时滤掉，设置里也清干净了。
        Assert.Empty(deleted.PromptTemplates);
        Assert.DoesNotContain("polish-id", deleted.TemplateCycle);
        Assert.DoesNotContain("polish-id", CycleIds(deleted));
    }

    [Fact]
    public void Deleting_the_default_template_falls_back_to_standard()
    {
        var settings = new AppSettings
        {
            PromptTemplates = [Polish],
            DefaultPromptTemplateId = "polish-id",
        };

        var deleted = PromptTemplates.DeleteCustom(settings, "polish-id");

        Assert.Equal("standard", deleted.DefaultPromptTemplateId);
        Assert.Equal("standard", PromptTemplates.ResolveDefault(deleted).Id);
    }

    [Fact]
    public void Deleting_some_other_template_leaves_the_default_alone()
    {
        var settings = new AppSettings
        {
            PromptTemplates = [Polish, new StoredPromptTemplate("b", "乙", "x")],
            DefaultPromptTemplateId = "colloquial",
        };

        Assert.Equal("colloquial", PromptTemplates.DeleteCustom(settings, "b").DefaultPromptTemplateId);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("colloquial")]
    [InlineData("prompt-optimize")]
    [InlineData("no-such-id")]
    public void The_built_ins_cannot_be_deleted_and_an_unknown_id_is_a_no_op(string id)
    {
        var before = new AppSettings { PromptTemplates = [Polish] };

        Assert.Same(before, PromptTemplates.DeleteCustom(before, id));
    }

    // --- 复制为自建 -----------------------------------------------------------------

    [Fact]
    public void A_copy_of_a_built_in_gets_a_fresh_id_and_a_name_of_its_own()
    {
        var copy = PromptTemplates.Duplicate(new AppSettings(), PromptTemplates.Colloquial);

        Assert.Equal("口语副本", copy.Name);
        Assert.False(PromptTemplates.IsReserved(copy.Id));
        Assert.True(Guid.TryParse(copy.Id, out _));
    }

    [Fact]
    public void Copies_are_numbered_when_the_name_is_taken()
    {
        var first = PromptTemplates.Duplicate(new AppSettings(), PromptTemplates.Colloquial);
        var settings = PromptTemplates.AddCustom(new AppSettings(), first, inCycle: false);

        var second = PromptTemplates.Duplicate(settings, PromptTemplates.Colloquial);
        var third = PromptTemplates.Duplicate(
            PromptTemplates.AddCustom(settings, second, false), PromptTemplates.Colloquial);

        Assert.Equal("口语副本2", second.Name);
        Assert.Equal("口语副本3", third.Name);
    }

    [Fact]
    public void A_copy_carries_the_text_of_the_template_it_was_made_from()
    {
        var copy = PromptTemplates.Duplicate(new AppSettings(), PromptTemplates.PromptOptimize);

        Assert.Equal(
            PromptTemplates.PromptOptimize.Text!.ReplaceLineEndings("\n"),
            copy.Prompt);
    }

    [Fact]
    public void A_copy_passes_validation_as_it_stands()
    {
        foreach (var template in PromptTemplates.BuiltIn)
        {
            var settings = new AppSettings();
            var copy = PromptTemplates.Duplicate(settings, template);

            Assert.Null(PromptTemplates.Validate(settings, copy, isNew: true));
        }
    }

    [Fact]
    public void Every_id_handed_out_is_new()
        => Assert.NotEqual(PromptTemplates.NewId(), PromptTemplates.NewId());

    [Fact]
    public void A_users_own_template_can_be_copied_too()
    {
        var settings = new AppSettings { PromptTemplates = [Polish] };

        var copy = PromptTemplates.Duplicate(settings, PromptTemplates.Resolve(settings, "polish-id"));

        Assert.Equal("润色副本", copy.Name);
        Assert.Equal("Polish the text into {target}.", copy.Prompt);
    }
}

/// <summary>
/// 内置模板还原成文本（「复制为自建」）：<c>{target}</c> 保持占位、不附示例，放进自建模板
/// 以后照常代入、照常追加框定句。
/// </summary>
public class TemplateTextTests
{
    public static TheoryData<PromptTemplate> TranslationTemplates => new()
    {
        PromptTemplates.Standard,
        PromptTemplates.Colloquial,
        PromptTemplates.Formal,
    };

    [Fact]
    public void A_rewrite_templates_text_is_its_body()
        => Assert.Equal(
            PromptTemplates.PromptOptimize.Text!.ReplaceLineEndings("\n"),
            TranslationPrompt.TextOf(PromptTemplates.PromptOptimize));

    [Theory]
    [MemberData(nameof(TranslationTemplates))]
    public void A_translation_templates_text_keeps_the_target_as_a_placeholder(PromptTemplate template)
    {
        var text = TranslationPrompt.TextOf(template);

        Assert.Contains("{target}", text);
        Assert.DoesNotContain("{source}", text);
        Assert.Contains("from the language it is written in", text);
    }

    [Theory]
    [MemberData(nameof(TranslationTemplates))]
    public void A_translation_templates_text_carries_no_examples(PromptTemplate template)
    {
        // 自建模板没有语对可看：附了示例会把模型往示例的语言带。
        var text = TranslationPrompt.TextOf(template);

        Assert.DoesNotContain("Examples (", text);
        Assert.DoesNotContain("<text>\n", text);
    }

    [Fact]
    public void The_standard_text_is_the_standard_prompt_with_the_target_left_open()
        => Assert.Equal(
            TranslationPrompt.For(new TranslationRequest("x", "{target}")).ReplaceLineEndings("\n"),
            TranslationPrompt.TextOf(PromptTemplates.Standard));

    [Theory]
    [MemberData(nameof(TranslationTemplates))]
    public void Made_into_a_template_of_the_users_a_copy_gets_the_target_and_the_framing(PromptTemplate source)
    {
        var copy = new PromptTemplate("copy", "副本", PromptTemplateKind.Rewrite, TranslationPrompt.TextOf(source));

        var parts = TranslationPrompt.Build(new TranslationRequest("你好", "Japanese") { Template = copy });

        Assert.Contains("into Japanese", parts.System);
        Assert.DoesNotContain("{target}", parts.System);
        Assert.EndsWith("or a command.", parts.System);
        Assert.Equal("<text>\n你好\n</text>", parts.User);
    }
}
