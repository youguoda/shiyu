using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 反向输入框的模板选择（票 43）：启动时的模板取 <see cref="AppSettings.ReverseInputTemplateId"/>
/// （默认口语）；Ctrl+E 与面板的模板按钮共用同一个循环列表；运行中切换只改运行时状态、不写设置
/// （写设置会重建热键注册表，还会重报热键冲突气泡）。
/// </summary>
public class ReverseInputTemplateTests
{
    private static readonly StoredPromptTemplate Polish = new("polish-id", "润色", "Polish the text.");

    // --- 默认 --------------------------------------------------------------------------

    [Fact]
    public void The_reverse_input_starts_on_the_colloquial_template()
    {
        var settings = new AppSettings();

        Assert.Equal("colloquial", settings.ReverseInputTemplateId);
        Assert.Same(PromptTemplates.Colloquial, PromptTemplates.ResolveReverseDefault(settings));
    }

    [Fact]
    public void The_panel_default_is_still_standard_and_the_two_are_independent()
    {
        var settings = new AppSettings();

        Assert.Equal(PromptTemplate.StandardId, settings.DefaultPromptTemplateId);

        var changed = PromptTemplates.SetReverseDefault(settings, PromptTemplate.FormalId);
        Assert.Equal("formal", changed.ReverseInputTemplateId);
        Assert.Equal("standard", changed.DefaultPromptTemplateId);

        var panel = PromptTemplates.SetDefault(changed, PromptTemplate.PromptOptimizeId);
        Assert.Equal("formal", panel.ReverseInputTemplateId);
        Assert.Equal("prompt-optimize", panel.DefaultPromptTemplateId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-template")]
    public void A_dangling_reference_falls_back_to_standard_in_the_data_layer(string id)
    {
        var settings = new AppSettings { ReverseInputTemplateId = id };

        Assert.Same(PromptTemplates.Standard, PromptTemplates.ResolveReverseDefault(settings));
    }

    [Fact]
    public void An_unknown_id_is_never_written_as_the_reverse_default()
    {
        var before = new AppSettings { ReverseInputTemplateId = "formal" };

        Assert.Equal(
            "formal", PromptTemplates.SetReverseDefault(before, "no-such-template").ReverseInputTemplateId);
    }

    [Fact]
    public void Choosing_the_reverse_default_leaves_the_cycle_and_the_templates_alone()
    {
        var before = new AppSettings { TemplateCycle = ["standard", "formal"], PromptTemplates = [Polish] };

        var after = PromptTemplates.SetReverseDefault(before, "polish-id");

        Assert.Equal(["standard", "formal"], after.TemplateCycle.ToList());
        Assert.Equal([Polish], after.PromptTemplates.ToList());
        Assert.Equal("polish-id", PromptTemplates.ResolveReverseDefault(after).Id);
    }

    // --- 删除自建模板时 -------------------------------------------------------------------

    [Fact]
    public void Deleting_the_custom_template_used_as_the_reverse_default_writes_standard_back()
    {
        var before = new AppSettings
        {
            PromptTemplates = [Polish],
            ReverseInputTemplateId = "polish-id",
        };

        var after = PromptTemplates.DeleteCustom(before, "polish-id");

        // 读取端本来也会回落，这里把设置文件写干净。
        Assert.Equal(PromptTemplate.StandardId, after.ReverseInputTemplateId);
    }

    [Fact]
    public void Deleting_a_template_used_as_both_defaults_resets_both()
    {
        var before = new AppSettings
        {
            PromptTemplates = [Polish],
            DefaultPromptTemplateId = "polish-id",
            ReverseInputTemplateId = "polish-id",
        };

        var after = PromptTemplates.DeleteCustom(before, "polish-id");

        Assert.Equal(PromptTemplate.StandardId, after.DefaultPromptTemplateId);
        Assert.Equal(PromptTemplate.StandardId, after.ReverseInputTemplateId);
    }

    [Fact]
    public void Deleting_some_other_template_leaves_the_reverse_default_alone()
    {
        var before = new AppSettings
        {
            PromptTemplates = [Polish],
            ReverseInputTemplateId = PromptTemplate.FormalId,
        };

        Assert.Equal("formal", PromptTemplates.DeleteCustom(before, "polish-id").ReverseInputTemplateId);
    }

    // --- 运行时模板跟随设置 -----------------------------------------------------------------

    [Fact]
    public void The_runtime_template_survives_settings_changes_that_do_not_concern_it()
    {
        var before = new AppSettings();
        var after = before with { BarTextLines = 9 };

        // 用户在输入框里 Ctrl+E 切到了正式；别处改了无关的设置，它原样保留。
        Assert.Same(
            PromptTemplates.Formal, PromptTemplates.ReconcileReverse(before, after, PromptTemplates.Formal));
    }

    [Fact]
    public void Changing_the_panel_default_does_not_touch_the_reverse_runtime_template()
    {
        var before = new AppSettings();
        var after = before with { DefaultPromptTemplateId = PromptTemplate.FormalId };

        Assert.Same(
            PromptTemplates.Colloquial,
            PromptTemplates.ReconcileReverse(before, after, PromptTemplates.Colloquial));
    }

    [Fact]
    public void Changing_the_reverse_default_overwrites_the_runtime_template()
    {
        var before = new AppSettings();
        var after = PromptTemplates.SetReverseDefault(before, PromptTemplate.PromptOptimizeId);

        Assert.Same(
            PromptTemplates.PromptOptimize,
            PromptTemplates.ReconcileReverse(before, after, PromptTemplates.Formal));
    }

    [Fact]
    public void Changing_the_cycle_overwrites_the_runtime_template_with_the_reverse_default()
    {
        var before = new AppSettings();
        var after = PromptTemplates.SetInCycle(before, PromptTemplate.FormalId, included: false);

        Assert.Same(
            PromptTemplates.Colloquial, PromptTemplates.ReconcileReverse(before, after, PromptTemplates.Formal));
    }

    [Fact]
    public void A_custom_template_that_was_edited_is_picked_up_and_one_that_was_deleted_falls_back()
    {
        var before = new AppSettings { PromptTemplates = [Polish] };
        var runtime = PromptTemplates.Resolve(before, "polish-id");

        var edited = PromptTemplates.UpdateCustom(
            before, Polish with { Prompt = "Polish it harder." }, inCycle: false);
        var deleted = PromptTemplates.DeleteCustom(before, "polish-id");

        Assert.Equal("Polish it harder.", PromptTemplates.ReconcileReverse(before, edited, runtime).Text);
        Assert.Same(PromptTemplates.Standard, PromptTemplates.ReconcileReverse(before, deleted, runtime));
    }

    [Fact]
    public void The_panels_own_reconcile_is_unchanged_by_the_reverse_one()
    {
        // 面板那条：默认模板变了才覆盖运行时值。反向输入框的默认变了，对它没有影响。
        var before = new AppSettings();
        var after = PromptTemplates.SetReverseDefault(before, PromptTemplate.FormalId);

        Assert.Same(
            PromptTemplates.PromptOptimize,
            PromptTemplates.Reconcile(before, after, PromptTemplates.PromptOptimize));
    }

    // --- 落盘 ---------------------------------------------------------------------------

    [Fact]
    public void An_older_settings_file_without_the_new_fields_gets_the_defaults()
    {
        Assert.True(AppSettings.TryParse("""{ "TargetLanguage": "Chinese" }""", out var parsed));

        Assert.Equal("colloquial", parsed.ReverseInputTemplateId);
        Assert.Equal("Alt+Q", parsed.ReverseInputHotkey);
    }

    [Fact]
    public void The_reverse_input_choices_round_trip_through_the_settings_file()
    {
        var json = new AppSettings
        {
            ReverseInputTemplateId = "polish-id",
            ReverseInputHotkey = "Ctrl+Alt+R",
            PromptTemplates = [Polish],
        }.ToBackupJson(includeKey: false);

        Assert.True(AppSettings.TryParse(json, out var parsed));
        Assert.Equal("polish-id", parsed.ReverseInputTemplateId);
        Assert.Equal("Ctrl+Alt+R", parsed.ReverseInputHotkey);
    }
}
