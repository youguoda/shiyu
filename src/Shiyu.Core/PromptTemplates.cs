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

    /// <summary>自建提示词的字数上限：再长的多半是误粘了一整篇文章。</summary>
    public const int MaxPromptLength = 4000;

    /// <summary>新自建模板的 id：GUID 字符串，终身稳定。</summary>
    public static string NewId() => Guid.NewGuid().ToString();

    /// <summary>全部可用的模板：内置在前、只读，之后是自建的。</summary>
    public static IReadOnlyList<PromptTemplate> All(AppSettings settings)
        => [.. BuiltIn, .. Custom(settings)];

    /// <summary>
    /// 设置里存着的自建模板，转成改写类模板。文件被手改坏时能用的留着、其余当作
    /// 不存在：null 条目、缺 id/名字/正文、占了保留 id、id 重复，都不抛、也不整体丢弃。
    /// </summary>
    public static IReadOnlyList<PromptTemplate> Custom(AppSettings settings)
    {
        var templates = new List<PromptTemplate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stored in settings.PromptTemplates ?? [])
        {
            // seen.Add 排在最后：一条坏条目不该占掉后面好条目的 id。
            if (stored is null
                || string.IsNullOrWhiteSpace(stored.Id)
                || IsReserved(stored.Id)
                || string.IsNullOrWhiteSpace(stored.Name)
                || string.IsNullOrWhiteSpace(stored.Prompt)
                || !seen.Add(stored.Id))
            {
                continue;
            }

            templates.Add(new PromptTemplate(
                stored.Id, stored.Name.Trim(), PromptTemplateKind.Rewrite, stored.Prompt));
        }

        return templates;
    }

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

    // --- 自建模板（阶段二）：校验与写设置 -----------------------------------------------
    //
    // 写设置的逻辑都在这里（纯函数，有测试），设置里的编辑器只负责收集三项输入、
    // 把校验的话说给用户、把结果交给 SettingsStore。不暴露温度和模型：CONTEXT.md
    // 说了不做高级参数面板。

    /// <summary>
    /// 校验一份自建模板；合法答 null，否则答一句人话。先查 id（保留 id 不可占用），再查
    /// 名字（非空、不与任何模板重名——不区分大小写，内置模板也算在内；编辑时不与自己
    /// 比），最后查提示词（非空、不超过 <see cref="MaxPromptLength"/> 字）。
    /// </summary>
    /// <param name="isNew">新建（id 不得已被占用）还是编辑（id 必须已存在）。</param>
    public static string? Validate(AppSettings settings, StoredPromptTemplate candidate, bool isNew)
    {
        var id = candidate.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return "模板标识不能为空。";
        }

        if (IsReserved(id))
        {
            return "这个标识是内置模板保留的，不能占用。";
        }

        var exists = (settings.PromptTemplates ?? []).Any(stored => stored?.Id == id);
        if (isNew && exists)
        {
            return "这个标识已经有模板在用了。";
        }

        if (!isNew && !exists)
        {
            return "要修改的模板不存在（可能已被删除）。";
        }

        var name = candidate.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return "名字不能为空。";
        }

        if (All(settings).Any(template => template.Id != id
                && string.Equals(template.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"已经有叫「{name}」的模板了（内置模板也算在内），换一个名字。";
        }

        var prompt = candidate.Prompt?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            return "提示词不能为空。";
        }

        if (prompt.Length > MaxPromptLength)
        {
            return $"提示词最多 {MaxPromptLength} 字，现在有 {prompt.Length} 字。";
        }

        return null;
    }

    /// <summary>
    /// 新建：校验不过原样返回设置（调用方先 <see cref="Validate"/> 取话说）。名字与提示词
    /// 收掉首尾空白再存。<paramref name="inCycle"/> 即编辑框里的"出现在切换里"。
    /// </summary>
    public static AppSettings AddCustom(AppSettings settings, StoredPromptTemplate template, bool inCycle)
    {
        if (Validate(settings, template, isNew: true) is not null)
        {
            return settings;
        }

        var stored = Tidy(settings).Append(Trimmed(template)).ToList();
        return SetInCycle(settings with { PromptTemplates = stored }, template.Id, inCycle);
    }

    /// <summary>编辑：按 id 原位替换名字与提示词，并按"出现在切换里"改循环。</summary>
    public static AppSettings UpdateCustom(AppSettings settings, StoredPromptTemplate template, bool inCycle)
    {
        if (Validate(settings, template, isNew: false) is not null)
        {
            return settings;
        }

        var stored = Tidy(settings)
            .Select(existing => existing.Id == template.Id ? Trimmed(template) : existing)
            .ToList();
        return SetInCycle(settings with { PromptTemplates = stored }, template.Id, inCycle);
    }

    /// <summary>
    /// 删除：从自建列表与循环里拿掉；它正被用作默认时，默认回落为标准（读取端本来也会
    /// 回落，这里把设置文件写干净）。内置模板与不存在的 id 原样返回——内置只读。
    /// </summary>
    public static AppSettings DeleteCustom(AppSettings settings, string id)
    {
        if (IsReserved(id) || !(settings.PromptTemplates ?? []).Any(stored => stored?.Id == id))
        {
            return settings;
        }

        return settings with
        {
            PromptTemplates = Tidy(settings).Where(stored => stored.Id != id).ToList(),
            TemplateCycle = (settings.TemplateCycle ?? []).Where(entry => entry != id).ToList(),
            DefaultPromptTemplateId = settings.DefaultPromptTemplateId == id
                ? PromptTemplate.StandardId
                : settings.DefaultPromptTemplateId,
        };
    }

    /// <summary>
    /// 「复制为自建」：给任何一个模板（内置的、自建的）做一份副本——新 id、不重名的名字
    /// （"口语副本"，重了就"口语副本2"）、提示词文本是它还原出来的文本。副本是还没存的
    /// 候选，交给 <see cref="AddCustom"/> 才算数。
    /// </summary>
    public static StoredPromptTemplate Duplicate(AppSettings settings, PromptTemplate source)
    {
        var taken = All(settings).Select(template => template.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = source.Name + "副本";
        for (var number = 2; taken.Contains(name); number++)
        {
            name = $"{source.Name}副本{number}";
        }

        return new StoredPromptTemplate(NewId(), name, TranslationPrompt.TextOf(source));
    }

    /// <summary>设置里存着的自建条目，去掉 null（手改坏的文件里可能有）。</summary>
    private static IEnumerable<StoredPromptTemplate> Tidy(AppSettings settings)
        => (settings.PromptTemplates ?? []).Where(stored => stored is not null);

    private static StoredPromptTemplate Trimmed(StoredPromptTemplate template)
        => template with { Name = template.Name.Trim(), Prompt = template.Prompt.Trim() };

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
