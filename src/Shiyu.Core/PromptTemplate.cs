namespace Shiyu.Core;

/// <summary>
/// 提示词模板的两类（票 42）。翻译类译成译文语言、换个语气；改写类按模板自己的
/// 指令处理文本，输出由模板决定。两类在回声重试、输出清洗、温度、防执行几处
/// 待遇不同，见 <see cref="TranslationPrompt"/>、<see cref="TranslationCleanup"/>
/// 与 <see cref="TranslationSession"/>。
/// </summary>
public enum PromptTemplateKind
{
    /// <summary>标准 / 口语 / 正式：译成译文语言，换个语气。</summary>
    Translate,

    /// <summary>提示词优化与全部自建模板：按模板自己的指令输出。</summary>
    Rewrite,
}

/// <summary>
/// 决定面板与反向输入框怎样处理当下这段文字的一段 prompt（CONTEXT.md「提示词模板」）。
///
/// 内置翻译类的 <see cref="Text"/> 为 null——它们的 system 由代码按规则与示例
/// 拼出（要看语对）；改写类的 Text 就是模板正文，其中的 <c>{target}</c> 代入
/// 译文语言。传给 <see cref="TranslationRequest"/> 的是解析好的模板对象而不是
/// id，Core 不必回头去读设置。
/// </summary>
public sealed record PromptTemplate(string Id, string Name, PromptTemplateKind Kind, string? Text)
{
    // 四个内置模板的 id 固定，保留下来，不给自建模板用——它们写进设置文件，
    // 改名就等于丢掉存量用户的选择。
    public const string StandardId = "standard";
    public const string ColloquialId = "colloquial";
    public const string FormalId = "formal";
    public const string PromptOptimizeId = "prompt-optimize";

    /// <summary>改写类模板的正文里写了它，就代入译文语言；不写就不用。</summary>
    public const string TargetPlaceholder = "{target}";

    /// <summary>出厂的循环：面板模板按钮与反向输入框的 Ctrl+E 共用这一份有序列表。</summary>
    public static readonly IReadOnlyList<string> DefaultCycle =
        [StandardId, ColloquialId, FormalId, PromptOptimizeId];

    /// <summary>
    /// 改写类的采样温度。改写结果与原文相近是正常的、措辞可以活一点，所以比翻译
    /// 默认的 0.2 略高；不暴露给用户（CONTEXT.md：不做高级参数面板）。
    /// </summary>
    public const double RewriteTemperature = 0.3;

    /// <summary>
    /// 这个模板的采样温度：标准、正式 0.2，口语 0.3，改写类 0.3。请求没有自己指定
    /// 温度时跟随它（见 <see cref="TranslationRequest.Temperature"/>）。
    /// </summary>
    public double Temperature { get; init; } =
        Kind == PromptTemplateKind.Rewrite ? RewriteTemperature : TranslationPrompt.DefaultTemperature;

    /// <summary>
    /// 模板是否用到译文语言。翻译类一定用到；改写类看正文写没写 <c>{target}</c>。
    /// 面板据此决定头部显示"中文 ⇄ 英语 · 口语"还是只显示模板名。
    /// </summary>
    public bool UsesTarget =>
        Kind == PromptTemplateKind.Translate
        || Text?.Contains(TargetPlaceholder, StringComparison.Ordinal) == true;

    /// <summary>是不是四个内置模板之一（只读，设置里可以「复制为自建」）。</summary>
    public bool IsBuiltIn => PromptTemplates.IsReserved(Id);
}
