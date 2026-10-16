namespace Shiyu.Core;

/// <summary>词典卡的一条义项：词性、释义、例句、同义词。</summary>
/// <param name="PartOfSpeech">词性（free 词典给英文，LLM 兜底给中文，本地词典给 n. vt. 这样的缩写）。</param>
public sealed record DictionarySense(
    string? PartOfSpeech,
    IReadOnlyList<string> Definitions,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string> Synonyms);

/// <summary>
/// 单词词典卡：查词场景从 LLM 泛答升级出来的那个“词典精度”的载体。
///
/// 它天生是紧凑卡——面板里一屏读完，不是词典网站的词条页。上限写进
/// 模型而不是渲染层，任何调用方拿到的都是同一张克制的卡。
/// </summary>
/// <param name="Phonetic">音标（英文 IPA；中文词是带调拼音）。查不到为 null。</param>
public sealed record DictionaryCard(
    string Word,
    string? Phonetic,
    IReadOnlyList<DictionarySense> Senses)
{
    public const int MaxSenses = 4;
    public const int MaxDefinitionsPerSense = 3;
    public const int MaxExamplesPerSense = 2;
    public const int MaxSynonymsPerSense = 4;

    /// <summary>考试标签（中考、四级、雅思……），本地词典给；别的来源没有。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>查的是变形时的原词（查 went 得到 go 的卡，这里是 went）；查的就是词头时为 null。</summary>
    public string? InflectionOf { get; init; }

    /// <summary>出自随程序发布的本地词典（评审 2026-10-16 F-08）：零延迟、断网可用，例句另行补充。</summary>
    public bool Offline { get; init; }
}

/// <summary>
/// 词典端口。查不到、超预算、网络坏、响应烂——对外一律是 null：词典卡
/// 是锦上添花，“没有卡”是它唯一允许的失败形态，永远不该变成报错。
/// </summary>
public interface IDictionaryApi
{
    Task<DictionaryCard?> LookupAsync(string word, CancellationToken cancellation = default);
}

/// <summary>
/// 卡上了屏之后还能补点什么的词典（评审 2026-10-16 F-08：本地卡先到，在线词典的例句后补）。补不出来是 null，
/// 与端口同一个契约，绝不抛。补的那一趟自己管自己的连接，与查卡的端口什么时候释放无关。
/// </summary>
public interface IDictionaryEnricher
{
    Task<DictionaryCard?> EnrichAsync(DictionaryCard card, CancellationToken cancellation = default);
}
