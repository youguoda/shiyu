namespace DictionaryBuilder;

/// <summary>入选条件。一个词命中其中任何一个就入选。</summary>
[Flags]
internal enum Criteria
{
    None = 0,
    Oxford = 1,    // oxford = 1：牛津 3000 核心词
    Collins = 2,   // collins >= 1：柯林斯星级词
    ExamTag = 4,   // tag 非空：中考 高考 考研 四六级 托福 雅思 GRE 之一
    Frequency = 8, // frq 或 bnc 名次在阈值以内
}

internal sealed record CollisionRecord(string Key, string Kept, string Reason, IReadOnlyList<string> Dropped);
internal sealed record DemotionRecord(string Form, string Lemma, string Translation);
internal sealed record KeptStubRecord(string Form, string Lemma, string Translation);
internal sealed record AmbiguousFormRecord(string Form, string Chosen, IReadOnlyList<string> Others);
internal sealed record PointerFormRecord(string Form, string Lemma);

/// <summary>构建过程中的各项计数，最后打印出来核对。</summary>
internal sealed class BuildStats
{
    // 读入与过滤
    public int Rows;                    // CSV 数据行数
    public int NotSingleWord;           // 单词形态不合格
    public int EmptyTranslation;        // 形态合格，但 translation 为空
    public int NoChineseTranslation;    // 形态合格，translation 非空但一个汉字都没有
    public int Eligible;                // 形态合格且有中文释义（候选）

    // 各条件在候选上的命中数（互相重叠）
    public int Oxford, Collins, ExamTag, Frequency;
    // 只靠这一个条件才入选的数量
    public int OnlyOxford, OnlyCollins, OnlyExamTag, OnlyFrequency;
    public int Selected;                // 命中任一条件

    // 大小写冲突
    public int EligibleCollisionKeys;   // 候选里折叠后同键的键数（仅供参考）
    public int CollisionKeys;           // 入选条目里同键的键数（真正要裁决的）
    public int CollisionEntriesDropped; // 因此被丢掉的条目数
    public int CollisionLowercaseWon;   // 其中靠"全小写"胜出
    public int CollisionFrequencyWon;   // 其中靠"词频"胜出

    // 变形条目降级
    public int Demoted;
    public int StubsKeptUncovered;      // 看起来是纯变形条目，但它的释义原形里没有，所以保留为词头

    // 结果
    public int Words;
    public int Forms;
    public int FormsFromNotes;          // 来自降级条目（释义明说"X 的过去式"）
    public int FormsOnlyFromPointers;   // 只有变形条目自己的 "0:" 项认领，原形的 exchange 里没列
    public int FormsAmbiguous;          // 被多个原形认领，按规则选了一个
    public int FormsSkippedHeadword;    // 因为这个词形本身是词头而没收的词形数
}

internal sealed class BuildResult
{
    /// <summary>最终词头，按 Key 排序。</summary>
    public required IReadOnlyList<EcdictEntry> Words { get; init; }

    /// <summary>词形 -> 原形，按词形排序。</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Forms { get; init; }

    public required BuildStats Stats { get; init; }
    public required IReadOnlyList<CollisionRecord> Collisions { get; init; }
    public required IReadOnlyList<DemotionRecord> Demotions { get; init; }
    public required IReadOnlyList<KeptStubRecord> KeptStubs { get; init; }
    public required IReadOnlyList<AmbiguousFormRecord> AmbiguousForms { get; init; }

    /// <summary>只有变形条目自己的 "0:" 项认领的词形（原形的 exchange 里没列）。</summary>
    public required IReadOnlyList<PointerFormRecord> PointerOnlyForms { get; init; }
}

/// <summary>
/// 把候选条目逐条喂进来（Add），最后（Finish）得到词头表和词形表。流程：
///   1. 命中任一入选条件的候选入选；
///   2. 折叠成小写后同键的入选条目只留一个（全小写的优先，否则词频最高的）；
///   3. 整条释义只是"X 的变形"的条目降级，不当词头，改走词形表（FindPureInflections 里有三道关，宁可多留）；
///   4. 建词形表（词形 -> 原形）：降级条目 + 入选词头的 exchange + 没入选的变形条目自己的 "0:" 项；
///      词形本身是词头的不收。
/// </summary>
internal sealed class WordListBuilder(int rankLimit)
{
    /// <summary>exchange 里表示"这是本词的某个词形"的类型：过去式 过去分词 现在分词 三单 比较级 最高级 复数。</summary>
    private const string FormKinds = "pdi3rts";

    private readonly List<EcdictEntry> _selected = [];

    // 没入选的变形条目 -> 它自己 "0:" 项指向的原形。原形的 exchange 里漏列的词形（如 were）靠它补上。
    private readonly List<(string Form, string Lemma)> _pointers = [];

    private readonly Dictionary<string, int> _eligibleKeyCounts = new(StringComparer.Ordinal);

    public BuildStats Stats { get; } = new();

    public Criteria Evaluate(EcdictEntry e)
    {
        var criteria = Criteria.None;
        if (e.Oxford) criteria |= Criteria.Oxford;
        if (e.Collins >= 1) criteria |= Criteria.Collins;
        if (e.Tag.Length > 0) criteria |= Criteria.ExamTag;
        if (e.Rank <= rankLimit) criteria |= Criteria.Frequency; // 没有名次时 Rank 是 int.MaxValue
        return criteria;
    }

    /// <summary>喂入一个候选条目（单词形态合格、有中文释义）。</summary>
    public void Add(EcdictEntry e)
    {
        Stats.Eligible++;
        _eligibleKeyCounts[e.Key] = _eligibleKeyCounts.GetValueOrDefault(e.Key) + 1;

        var criteria = Evaluate(e);
        if (criteria.HasFlag(Criteria.Oxford)) Stats.Oxford++;
        if (criteria.HasFlag(Criteria.Collins)) Stats.Collins++;
        if (criteria.HasFlag(Criteria.ExamTag)) Stats.ExamTag++;
        if (criteria.HasFlag(Criteria.Frequency)) Stats.Frequency++;

        if (criteria == Criteria.None)
        {
            foreach (var item in e.Exchange)
            {
                if (item.Kind == '0' && item.Value != e.Key) _pointers.Add((e.Key, item.Value));
            }
            return;
        }

        Stats.Selected++;
        switch (criteria)
        {
            case Criteria.Oxford: Stats.OnlyOxford++; break;
            case Criteria.Collins: Stats.OnlyCollins++; break;
            case Criteria.ExamTag: Stats.OnlyExamTag++; break;
            case Criteria.Frequency: Stats.OnlyFrequency++; break;
        }
        _selected.Add(e);
    }

    public BuildResult Finish()
    {
        Stats.EligibleCollisionKeys = _eligibleKeyCounts.Count(kv => kv.Value > 1);

        var collisions = new List<CollisionRecord>();
        var byKey = ResolveCollisions(collisions);

        var demotions = new List<DemotionRecord>();
        var keptStubs = new List<KeptStubRecord>();
        var demoted = FindPureInflections(byKey, demotions, keptStubs);
        foreach (var form in demoted.Keys) byKey.Remove(form);
        Stats.Demoted = demoted.Count;
        Stats.StubsKeptUncovered = keptStubs.Count;

        var ambiguous = new List<AmbiguousFormRecord>();
        var pointerOnly = new List<PointerFormRecord>();
        var forms = BuildForms(byKey, demoted, ambiguous, pointerOnly);

        var words = byKey.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        Stats.Words = words.Count;
        Stats.Forms = forms.Count;

        return new BuildResult
        {
            Words = words,
            Forms = forms.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(),
            Stats = Stats,
            Collisions = collisions,
            Demotions = demotions,
            KeptStubs = keptStubs,
            AmbiguousForms = ambiguous,
            PointerOnlyForms = pointerOnly,
        };
    }

    // ---- 第 2 步：大小写冲突 ----------------------------------------------------------------

    /// <summary>
    /// 同一个小写键有多个入选条目（Polish / polish）时只留一个：
    /// 有全小写的就留它；否则留词频最高的（名次最小），再依次比柯林斯星级、牛津、CSV 先后。
    /// 先判入选再判冲突是有意的：若小写条目是冷僻义项而大写条目才是常用义（Japan / japan），
    /// 冷僻的那个没入选，就不会把常用的挤掉。
    /// </summary>
    private Dictionary<string, EcdictEntry> ResolveCollisions(List<CollisionRecord> audit)
    {
        var byKey = new Dictionary<string, EcdictEntry>(StringComparer.Ordinal);
        foreach (var group in _selected.GroupBy(e => e.Key, StringComparer.Ordinal))
        {
            var candidates = group.OrderBy(e => e.Order).ToList();
            if (candidates.Count == 1)
            {
                byKey[group.Key] = candidates[0];
                continue;
            }

            Stats.CollisionKeys++;
            Stats.CollisionEntriesDropped += candidates.Count - 1;

            var lowercase = candidates.FirstOrDefault(e => e.Word == e.Key);
            EcdictEntry winner;
            string reason;
            if (lowercase is not null)
            {
                winner = lowercase;
                reason = "lowercase";
                Stats.CollisionLowercaseWon++;
            }
            else
            {
                winner = candidates
                    .OrderBy(e => e.Rank)
                    .ThenByDescending(e => e.Collins)
                    .ThenByDescending(e => e.Oxford)
                    .ThenBy(e => e.Order)
                    .First();
                reason = "frequency";
                Stats.CollisionFrequencyWon++;
            }

            byKey[group.Key] = winner;
            audit.Add(new CollisionRecord(group.Key, winner.Word, reason,
                candidates.Where(e => e != winner).Select(e => e.Word).ToList()));
        }
        return byKey;
    }

    // ---- 第 3 步：纯变形条目降级 ----------------------------------------------------------------

    /// <summary>
    /// 找出"整条释义只是 X 的变形说明，且 X 是入选词头"的条目，返回 词形 -> 原形。
    /// 三道关，全过才降级，否则它继续当词头（宁可多留，不可丢信息）：
    ///   1. 释义自称是 X 的变形（InflectionNote.StubLemma），saw / left / found / gone 这类有自己释义的词到这就被排除；
    ///   2. 结构化数据印证：本条自己的 "0:" 指向 X，或 X 的 exchange 列出了本词；
    ///   3. 本条的释义 X 里都有（InflectionNote.GlossCoveredBy），glasses（眼镜）这类有独立义项的复数被拦下。
    /// </summary>
    private Dictionary<string, string> FindPureInflections(
        Dictionary<string, EcdictEntry> byKey,
        List<DemotionRecord> audit,
        List<KeptStubRecord> keptStubs)
    {
        // 词形 -> 在 exchange 里列出它的原形们
        var listedBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var lemma in byKey.Values)
        {
            foreach (var item in lemma.Exchange)
            {
                if (!IsFormKind(item.Kind)) continue;
                if (!listedBy.TryGetValue(item.Value, out var owners)) listedBy[item.Value] = owners = new HashSet<string>(StringComparer.Ordinal);
                owners.Add(lemma.Key);
            }
        }

        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in byKey.Values)
        {
            var lemmaKey = InflectionNote.StubLemma(entry.Translation);
            if (lemmaKey is null || lemmaKey == entry.Key || !byKey.ContainsKey(lemmaKey)) continue;

            var confirmed = entry.Exchange.Any(i => i.Kind == '0' && i.Value == lemmaKey)
                            || (listedBy.TryGetValue(entry.Key, out var owners) && owners.Contains(lemmaKey));
            if (!confirmed) continue;

            if (!InflectionNote.GlossCoveredBy(entry.Translation, byKey[lemmaKey].Translation))
            {
                keptStubs.Add(new KeptStubRecord(entry.Key, lemmaKey, entry.Translation));
                continue;
            }
            candidates[entry.Key] = lemmaKey;
        }

        // 原形自己也被降级时（A 是 B 的变形，B 又是 C 的变形），顺着找到最终留下的词。
        // 链太长或成环就放弃这一条，让它继续当词头。
        var demoted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (form, firstLemma) in candidates)
        {
            var target = firstLemma;
            var hops = 0;
            while (candidates.TryGetValue(target, out var next) && hops++ < 4) target = next;
            if (candidates.ContainsKey(target)) continue;

            demoted[form] = target;
            audit.Add(new DemotionRecord(form, target, byKey[form].Translation));
        }
        return demoted;
    }

    // ---- 第 4 步：词形表 ----------------------------------------------------------------

    private enum ClaimSource { Note, ExchangeList, OwnPointer }

    private readonly record struct Claim(string Lemma, ClaimSource Source);

    /// <summary>
    /// 词形 -> 原形。三个来源：
    ///   Note         降级条目：释义明说它是谁的变形；
    ///   ExchangeList 入选词头的 exchange（p d i 3 r t s）；
    ///   OwnPointer   没入选的变形条目自己的 "0:" 项（补原形 exchange 里漏列的，如 were -> be）。
    /// 词形本身是词头的不收（saw 有自己的条目，查 saw 就给 saw）。
    /// 一个词形被多个原形认领时（leaves：leaf 的复数、leave 的三单）只能选一个：
    /// 释义明说的优先，其次词频最高的原形，最后按字母序，结果是确定的。
    /// </summary>
    private Dictionary<string, string> BuildForms(
        Dictionary<string, EcdictEntry> words,
        Dictionary<string, string> demoted,
        List<AmbiguousFormRecord> ambiguousAudit,
        List<PointerFormRecord> pointerOnlyAudit)
    {
        var claims = new Dictionary<string, List<Claim>>(StringComparer.Ordinal);
        var skippedHeadwords = new HashSet<string>(StringComparer.Ordinal);

        void AddClaim(string form, string lemma, ClaimSource source)
        {
            if (form == lemma || !EcdictRow.IsWordShape(form)) return;
            if (words.ContainsKey(form))
            {
                skippedHeadwords.Add(form);
                return;
            }
            if (!claims.TryGetValue(form, out var list)) claims[form] = list = [];
            list.Add(new Claim(lemma, source));
        }

        foreach (var (form, lemma) in demoted) AddClaim(form, lemma, ClaimSource.Note);

        foreach (var lemma in words.Values)
        {
            foreach (var item in lemma.Exchange)
            {
                if (IsFormKind(item.Kind)) AddClaim(item.Value, lemma.Key, ClaimSource.ExchangeList);
            }
        }

        foreach (var (form, lemma) in _pointers)
        {
            if (words.ContainsKey(lemma)) AddClaim(form, lemma, ClaimSource.OwnPointer);
        }

        Stats.FormsSkippedHeadword = skippedHeadwords.Count;

        var forms = new Dictionary<string, string>(claims.Count, StringComparer.Ordinal);
        foreach (var (form, list) in claims)
        {
            var distinct = list.Select(c => c.Lemma).Distinct(StringComparer.Ordinal).ToList();
            var chosen = list
                .OrderBy(c => c.Source == ClaimSource.Note ? 0 : 1)
                .ThenBy(c => words[c.Lemma].Rank)
                .ThenBy(c => c.Lemma, StringComparer.Ordinal)
                .First();

            if (distinct.Count > 1)
            {
                Stats.FormsAmbiguous++;
                ambiguousAudit.Add(new AmbiguousFormRecord(form, chosen.Lemma,
                    distinct.Where(l => l != chosen.Lemma).OrderBy(l => l, StringComparer.Ordinal).ToList()));
            }
            if (list.Any(c => c.Source == ClaimSource.Note)) Stats.FormsFromNotes++;
            if (list.All(c => c.Source == ClaimSource.OwnPointer))
            {
                Stats.FormsOnlyFromPointers++;
                pointerOnlyAudit.Add(new PointerFormRecord(form, chosen.Lemma));
            }

            forms[form] = chosen.Lemma;
        }
        return forms;
    }

    private static bool IsFormKind(char kind) => FormKinds.Contains(kind);
}
