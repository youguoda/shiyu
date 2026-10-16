using Microsoft.Data.Sqlite;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 离线词典（评审 2026-10-16 F-08）：英文词先查本地——中文释义、音标、考试标签，零延迟、断网可用；
/// 查不到再走原来的路；本地卡的例句由在线词典后补，补不上就没有。
/// </summary>
public class LocalDictionaryTests
{
    /// <summary>与生成器同一个表结构的一份小词典。</summary>
    private sealed class TinyDictionary : IDisposable
    {
        public TinyDictionary()
        {
            FilePath = Path.Combine(Path.GetTempPath(), "shiyu-dict-" + Guid.NewGuid().ToString("N") + ".db");
            using var connection = new SqliteConnection($"Data Source={FilePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE words(word TEXT PRIMARY KEY, phonetic TEXT, translation TEXT NOT NULL, tag TEXT) WITHOUT ROWID;
                CREATE TABLE forms(form TEXT PRIMARY KEY, lemma TEXT NOT NULL) WITHOUT ROWID;
                INSERT INTO words VALUES ('example', 'ig''zɑ:mpl', 'n. 例子, 样本, 实例' || char(10) || '[计] 实例', 'zk gk ielts');
                INSERT INTO words VALUES ('go', 'gәu', 'vi. 去, 走' || char(10) || 'n. 尝试', 'zk gk');
                INSERT INTO words VALUES ('dog', 'dɒ^', 'n. 狗', NULL);
                INSERT INTO words VALUES ('don''t', NULL, '(=do not)不要', NULL);
                INSERT INTO words VALUES ('well-known', NULL, 'a. 众所周知的', 'cet4 cet6');
                INSERT INTO forms VALUES ('went', 'go');
                INSERT INTO forms VALUES ('examples', 'example');
                """;
            command.ExecuteNonQuery();
            SqliteConnection.ClearAllPools();
        }

        public string FilePath { get; }

        public LocalDictionary Open() => new(FilePath);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(FilePath); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void A_headword_comes_back_with_chinese_senses_phonetic_and_exam_tags()
    {
        using var tiny = new TinyDictionary();

        var card = tiny.Open().Lookup("example");

        Assert.NotNull(card);
        Assert.Equal("example", card!.Word);
        Assert.Equal("/igˈzɑːmpl/", card.Phonetic);
        Assert.Equal(["中考", "高考", "雅思"], card.Tags);
        Assert.True(card.Offline);
        Assert.Null(card.InflectionOf);
        Assert.Equal("n.", card.Senses[0].PartOfSpeech);
        Assert.Equal(["例子, 样本, 实例"], card.Senses[0].Definitions);
        Assert.Equal("[计]", card.Senses[1].PartOfSpeech);
        Assert.Equal(["实例"], card.Senses[1].Definitions);
    }

    [Fact]
    public void An_inflected_form_brings_its_lemma_and_says_so()
    {
        using var tiny = new TinyDictionary();

        var card = tiny.Open().Lookup("went");

        Assert.Equal("go", card!.Word);
        Assert.Equal("went", card.InflectionOf);
        Assert.Equal("/gəu/", card.Phonetic);
    }

    [Theory]
    [InlineData("Example", "example")]
    [InlineData("  examples. ", "example")]
    [InlineData("“example”", "example")]
    [InlineData("don’t", "don't")]
    [InlineData("dog's", "dog")]
    [InlineData("well‑known", "well-known")]
    public void What_a_selection_drags_along_does_not_stop_the_lookup(string selected, string headword)
    {
        using var tiny = new TinyDictionary();

        Assert.Equal(headword, tiny.Open().Lookup(selected)?.Word);
    }

    [Fact]
    public void An_unknown_word_or_a_missing_file_is_just_no_card()
    {
        using var tiny = new TinyDictionary();

        Assert.Null(tiny.Open().Lookup("serendipity"));
        Assert.Null(new LocalDictionary(Path.Combine(Path.GetTempPath(), "no-such-dictionary.db")).Lookup("example"));
    }

    [Fact]
    public void A_damaged_file_is_no_card_rather_than_an_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "shiyu-dict-broken-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllText(path, "this is not a database");
        try
        {
            Assert.Null(new LocalDictionary(path).Lookup("example"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Ecdict_phonetics_read_as_ipa()
    {
        Assert.Equal("/ˌserənˈdipiti/", LocalDictionary.Ipa(",serәn'dipiti"));
        Assert.Equal("/dɒɡ/", LocalDictionary.Ipa("dɒ^"));
        Assert.Equal("/ə; eɪ/", LocalDictionary.Ipa("ә;eɪ"));
        Assert.Null(LocalDictionary.Ipa(null));
        Assert.Null(LocalDictionary.Ipa("  "));
    }

    [Fact]
    public void Senses_keep_general_meanings_ahead_of_domain_ones_and_stop_at_four()
    {
        var senses = LocalDictionary.Senses("[医] 甲\nn. 乙\n[计] 丙\nvt. 丁\nvi. 戊\na. 己");

        Assert.Equal(DictionaryCard.MaxSenses, senses.Count);
        Assert.Equal(["n.", "vt.", "vi.", "a."], senses.Select(sense => sense.PartOfSpeech));
        Assert.Equal("[医]", LocalDictionary.Senses("[医] 甲")[0].PartOfSpeech);
    }

    // ── 本地优先与后补例句 ──

    private sealed class Scripted(DictionaryCard? card, bool throws = false) : IDictionaryApi
    {
        public int Calls { get; private set; }

        public Task<DictionaryCard?> LookupAsync(string word, CancellationToken cancellation = default)
        {
            Calls++;
            return throws ? throw new HttpRequestException("offline") : Task.FromResult(card);
        }
    }

    [Fact]
    public async Task A_local_hit_never_waits_for_the_network()
    {
        using var tiny = new TinyDictionary();
        var online = new Scripted(new DictionaryCard("example", "/x/", []));

        var card = await new LocalFirstDictionary(tiny.Open(), online, null).LookupAsync("example");

        Assert.True(card!.Offline);
        Assert.Equal(0, online.Calls);
    }

    [Fact]
    public async Task A_local_miss_takes_the_old_road()
    {
        using var tiny = new TinyDictionary();
        var online = new Scripted(new DictionaryCard("serendipity", "/x/", []));

        var card = await new LocalFirstDictionary(tiny.Open(), online, null).LookupAsync("serendipity");

        Assert.Equal("serendipity", card!.Word);
        Assert.False(card.Offline);
        Assert.Equal(1, online.Calls);
    }

    [Fact]
    public async Task Examples_from_the_online_dictionary_land_under_the_matching_part_of_speech()
    {
        using var tiny = new TinyDictionary();
        var local = tiny.Open().Lookup("go")!;
        var online = new Scripted(new DictionaryCard("go", "/ɡəʊ/",
        [
            new DictionarySense("noun", ["an attempt"], ["Have a go."], []),
            new DictionarySense("verb", ["to move"], ["We go home.", "Go away!", "Let's go."], []),
        ]));

        var richer = await new LocalFirstDictionary(tiny.Open(), null, () => online).EnrichAsync(local);

        Assert.NotNull(richer);
        Assert.Equal(["We go home.", "Go away!"], richer!.Senses[0].Examples);
        Assert.Equal(["Have a go."], richer.Senses[1].Examples);
        Assert.Equal(local.Senses[0].Definitions, richer.Senses[0].Definitions);
    }

    [Fact]
    public async Task Without_examples_or_without_network_there_is_nothing_to_add()
    {
        using var tiny = new TinyDictionary();
        var local = tiny.Open().Lookup("example")!;

        Assert.Null(await new LocalFirstDictionary(tiny.Open(), null, () => new Scripted(null)).EnrichAsync(local));
        Assert.Null(await new LocalFirstDictionary(tiny.Open(), null, () => new Scripted(null, throws: true)).EnrichAsync(local));
        Assert.Null(await new LocalFirstDictionary(tiny.Open(), null, null).EnrichAsync(local));

        // 不是本地卡就不补：在线词典的卡自己就带着例句。
        Assert.Null(await new LocalFirstDictionary(tiny.Open(), null, () => new Scripted(local))
            .EnrichAsync(local with { Offline = false }));
    }

    // ── 随程序发布的那一份 ──

    private static string ShippedDictionary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Shiyu.Core")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("找不到仓库根"), "src", "Shiyu.App", "Assets", "dictionary.db");
    }

    [Fact]
    public void The_shipped_dictionary_knows_example_offline()
    {
        var card = new LocalDictionary(ShippedDictionary()).Lookup("example");

        Assert.NotNull(card);
        Assert.Equal("/igˈzæmpl/", card!.Phonetic);
        Assert.Contains(card.Senses, sense => sense.Definitions.Any(text => text.Contains("例子")));
        Assert.Contains("高考", card.Tags);
    }

    [Theory]
    [InlineData("went", "go")]
    [InlineData("examples", "example")]
    [InlineData("ran", "run")]
    [InlineData("studies", "study")]
    public void The_shipped_dictionary_resolves_common_inflections(string form, string lemma)
    {
        var card = new LocalDictionary(ShippedDictionary()).Lookup(form);

        Assert.Equal(lemma, card?.Word);
    }
}
