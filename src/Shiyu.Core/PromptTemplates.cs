namespace Shiyu.Core;

/// <summary>
/// 提示词模板库（票 42）：给出内置列表、按 id 解析（失效时回落标准）、根据设置算出
/// 循环列表。
///
/// 引用失效——模板被删、设置文件被手改坏——一律回落到标准，未知 id 从循环里滤掉。
/// 这些都在数据层解析，界面只负责显示：面板、设置窗、日后的反向输入框读到的是
/// 同一个答案。
/// </summary>
public static class PromptTemplates
{
    /// <summary>标准：与提示词模板落地之前逐字相同，默认路径零回归。</summary>
    public static readonly PromptTemplate Standard =
        new(PromptTemplate.StandardId, "标准", PromptTemplateKind.Translate, null);

    /// <summary>口语的采样温度取自 Xtranslate 验证过的 0.3：措辞要活，但仍是翻译。</summary>
    private const double ColloquialTemperature = 0.3;

    public static readonly PromptTemplate Colloquial =
        new(PromptTemplate.ColloquialId, "口语", PromptTemplateKind.Translate, null)
        {
            Temperature = ColloquialTemperature,
        };

    public static readonly PromptTemplate Formal =
        new(PromptTemplate.FormalId, "正式", PromptTemplateKind.Translate, null);

    public static readonly PromptTemplate PromptOptimize =
        new(PromptTemplate.PromptOptimizeId, "提示词优化", PromptTemplateKind.Rewrite, BuiltInPrompts.PromptOptimize);

    /// <summary>四个内置模板，顺序即设置里的显示顺序与出厂循环。</summary>
    public static readonly IReadOnlyList<PromptTemplate> BuiltIn = [Standard, Colloquial, Formal, PromptOptimize];

    /// <summary>四个内置 id 保留下来，不给自建模板用。</summary>
    public static bool IsReserved(string? id)
        => id is PromptTemplate.StandardId
            or PromptTemplate.ColloquialId
            or PromptTemplate.FormalId
            or PromptTemplate.PromptOptimizeId;

    /// <summary>全部可用的模板：内置在前、只读，之后是自建的。</summary>
    public static IReadOnlyList<PromptTemplate> All(AppSettings settings) => BuiltIn;

    /// <summary>按 id 解析；缺失、空白或不认识的 id 一律回落到标准。</summary>
    public static PromptTemplate Resolve(AppSettings settings, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Standard;
        }

        return All(settings).FirstOrDefault(template => template.Id == id) ?? Standard;
    }

    /// <summary>面板启动时、以及设置里的默认模板被改动时，用的那个模板。</summary>
    public static PromptTemplate ResolveDefault(AppSettings settings)
        => Resolve(settings, settings.DefaultPromptTemplateId);

    /// <summary>
    /// 循环列表：设置里存的 id 按原顺序解析，不认识的与重复的滤掉。面板的模板按钮
    /// 与反向输入框的 Ctrl+E 共用它。
    /// </summary>
    public static IReadOnlyList<PromptTemplate> Cycle(AppSettings settings)
    {
        var known = All(settings);
        var cycle = new List<PromptTemplate>();

        foreach (var id in settings.TemplateCycle ?? [])
        {
            if (string.IsNullOrWhiteSpace(id)
                || known.FirstOrDefault(template => template.Id == id) is not { } template
                || cycle.Any(existing => existing.Id == template.Id))
            {
                continue;
            }

            cycle.Add(template);
        }

        return cycle;
    }

    /// <summary>
    /// 把某个模板设为默认。不认识的 id 不写进设置——写一个悬空的引用没有任何好处，
    /// 读取端虽然会回落到标准，但设置文件里不该留下它。
    /// </summary>
    public static AppSettings SetDefault(AppSettings settings, string id)
        => All(settings).Any(template => template.Id == id)
            ? settings with { DefaultPromptTemplateId = id }
            : settings;

    /// <summary>
    /// 把某个模板放进或拿出循环。循环的顺序就是设置里列出的顺序（内置在前、之后是
    /// 自建的），所以关了再开不会跑到队尾；写入时顺手清掉失效的 id。
    /// 默认模板与"出现在切换里"是两个独立的开关，互不牵连。
    /// </summary>
    public static AppSettings SetInCycle(AppSettings settings, string id, bool included)
    {
        var all = All(settings);
        if (all.All(template => template.Id != id))
        {
            return settings;
        }

        var current = Cycle(settings).Select(template => template.Id).ToHashSet();
        var ids = all
            .Where(template => template.Id == id ? included : current.Contains(template.Id))
            .Select(template => template.Id)
            .ToList();

        return settings with { TemplateCycle = ids };
    }

    /// <summary>
    /// 循环里的下一个。当前模板不在循环里（比如默认模板没勾进切换）时从第一个开始；
    /// 循环为空则原地不动。
    /// </summary>
    public static PromptTemplate Next(PromptTemplate current, IReadOnlyList<PromptTemplate> cycle)
    {
        if (cycle.Count == 0)
        {
            return current;
        }

        var at = -1;
        for (var index = 0; index < cycle.Count; index++)
        {
            if (cycle[index].Id == current.Id)
            {
                at = index;
                break;
            }
        }

        return cycle[(at + 1) % cycle.Count];
    }

    /// <summary>
    /// 设置变了以后，面板的运行时模板该是哪个。运行时模板只活在进程里（面板里点
    /// 模板按钮不写设置），所以只有默认模板或循环列表真的变了，才用新的默认值覆盖
    /// 它；别处改了无关的设置，它原样保留。同一个 id 照常重新解析一遍，让自建模板
    /// 被改了正文、被删了之后，运行时的值跟着变（删了即回落到标准）。
    /// </summary>
    public static PromptTemplate Reconcile(AppSettings before, AppSettings after, PromptTemplate runtime)
    {
        var selectionChanged =
            ResolveDefault(before).Id != ResolveDefault(after).Id
            || !Cycle(before).Select(template => template.Id)
                .SequenceEqual(Cycle(after).Select(template => template.Id));

        return selectionChanged ? ResolveDefault(after) : Resolve(after, runtime.Id);
    }
}

/// <summary>
/// 面板头部与布局据此取舍：纯数据，WPF 只负责照着显示。
///
/// <list type="bullet">
/// <item>单词态（词典卡）不受模板影响，一律按标准翻译，按钮隐藏；</item>
/// <item>模板不生效（免费引擎没有 prompt）时同样隐藏按钮、按标准构建请求，
/// 免费引擎下回声换向照常工作；</item>
/// <item>模板含 <c>{target}</c> 时头部读作"中文 ⇄ 英语 · 口语"，不含时只显示模板名；</item>
/// <item>改写类模板下隐藏"整段 / 逐句"分段——逐句对照是给译文用的，改写结果和
/// 原文的句子对不上。</item>
/// </list>
/// </summary>
/// <param name="Template">实际发给后端的模板（单词态与不生效时是标准）。</param>
/// <param name="AccessibleName">按钮的无障碍名：用运行时模板的全名，随状态更新。</param>
public sealed record PanelTemplateState(
    PromptTemplate Template,
    string AccessibleName,
    bool ButtonVisible,
    bool DirectionVisible,
    bool ModeSegmentVisible)
{
    /// <param name="runtime">面板当下选着的模板（只活在进程里）。</param>
    /// <param name="templatesApply"><see cref="AppSettings.PromptTemplatesApply"/>。</param>
    /// <param name="wordMode">单词态：词典卡。</param>
    public static PanelTemplateState For(PromptTemplate runtime, bool templatesApply, bool wordMode)
    {
        var buttonVisible = templatesApply && !wordMode;
        var effective = buttonVisible ? runtime : PromptTemplates.Standard;

        return new PanelTemplateState(
            effective,
            $"提示词模板：{runtime.Name}，点击切换",
            buttonVisible,

            // 按钮不在时，方向标签照旧；在时看模板用不用得着译文语言。
            DirectionVisible: !buttonVisible || runtime.UsesTarget,

            // 单词态本来就没有模式分段；改写类固定为整段。
            ModeSegmentVisible: !wordMode && effective.Kind == PromptTemplateKind.Translate);
    }
}
