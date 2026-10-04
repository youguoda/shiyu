using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 设置里「提示词模板」控件的两个开关怎样落到设置上：设为默认、出现在切换里。写设置
/// 的逻辑放在 Core 里，界面只负责调用——这样不用起 WPF 也能钉住。
/// </summary>
public class PromptTemplateSelectionEditTests
{
    private static List<string> CycleIds(AppSettings settings)
        => PromptTemplates.Cycle(settings).Select(template => template.Id).ToList();

    // --- 设为默认 -----------------------------------------------------------------

    [Fact]
    public void A_known_template_becomes_the_default()
    {
        var edited = PromptTemplates.SetDefault(new AppSettings(), "colloquial");

        Assert.Equal("colloquial", edited.DefaultPromptTemplateId);
        Assert.Equal("colloquial", PromptTemplates.ResolveDefault(edited).Id);
    }

    [Fact]
    public void An_unknown_id_is_never_written_as_the_default()
    {
        var before = new AppSettings { DefaultPromptTemplateId = "formal" };

        Assert.Equal("formal", PromptTemplates.SetDefault(before, "no-such-template").DefaultPromptTemplateId);
    }

    [Fact]
    public void Choosing_a_default_leaves_the_cycle_alone()
    {
        var before = new AppSettings { TemplateCycle = ["standard", "formal"] };

        Assert.Equal(["standard", "formal"], PromptTemplates.SetDefault(before, "colloquial").TemplateCycle.ToList());
    }

    // --- 出现在切换里 -------------------------------------------------------------

    [Fact]
    public void Switching_a_template_off_takes_it_out_of_the_cycle()
    {
        var edited = PromptTemplates.SetInCycle(new AppSettings(), "formal", included: false);

        Assert.Equal(["standard", "colloquial", "prompt-optimize"], CycleIds(edited));
    }

    [Fact]
    public void Switching_it_back_on_restores_the_display_order()
    {
        var off = PromptTemplates.SetInCycle(new AppSettings(), "colloquial", included: false);
        var on = PromptTemplates.SetInCycle(off, "colloquial", included: true);

        // 循环的顺序就是设置里列出的顺序：关了再开，不会跑到队尾。
        Assert.Equal(["standard", "colloquial", "formal", "prompt-optimize"], CycleIds(on));
    }

    [Fact]
    public void Switching_on_a_template_already_in_the_cycle_changes_nothing()
    {
        var edited = PromptTemplates.SetInCycle(new AppSettings(), "formal", included: true);

        Assert.Equal(["standard", "colloquial", "formal", "prompt-optimize"], CycleIds(edited));
    }

    [Fact]
    public void An_unknown_id_leaves_the_cycle_untouched()
    {
        var before = new AppSettings { TemplateCycle = ["standard", "formal"] };

        Assert.Equal(["standard", "formal"], PromptTemplates.SetInCycle(before, "ghost", true).TemplateCycle.ToList());
    }

    [Fact]
    public void Stale_ids_are_cleaned_out_when_the_cycle_is_written()
    {
        var before = new AppSettings { TemplateCycle = ["standard", "ghost", "formal"] };

        var edited = PromptTemplates.SetInCycle(before, "colloquial", included: true);

        Assert.Equal(["standard", "colloquial", "formal"], edited.TemplateCycle.ToList());
    }

    [Fact]
    public void Every_template_can_be_switched_off_leaving_an_empty_cycle()
    {
        var edited = new AppSettings();
        foreach (var template in PromptTemplates.BuiltIn)
        {
            edited = PromptTemplates.SetInCycle(edited, template.Id, included: false);
        }

        Assert.Empty(edited.TemplateCycle);
        Assert.Empty(PromptTemplates.Cycle(edited));
    }

    [Fact]
    public void The_default_may_sit_outside_the_cycle()
    {
        // 默认与"出现在切换里"是两个独立的开关：默认模板不在循环里也成立，
        // 面板启动时用默认模板，点按钮从循环的第一个开始。
        var edited = PromptTemplates.SetInCycle(
            PromptTemplates.SetDefault(new AppSettings(), "formal"), "formal", included: false);

        Assert.Equal("formal", PromptTemplates.ResolveDefault(edited).Id);
        Assert.DoesNotContain("formal", CycleIds(edited));
    }
}
