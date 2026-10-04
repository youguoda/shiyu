using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 语言码表（票 41）：名字归一与 <see cref="LanguageDisplay"/> 共用同一套别名；
/// 八种语言映射到必应与腾讯各自的码。码值本身由网络探针各验一条
/// （tools/probes/probe-free-engines.ps1），这里钉住的是"表与设置项对得上"。
/// </summary>
public class FreeEngineLanguagesTests
{
    [Theory]
    [InlineData("Chinese", "zh-Hans", "zh")]
    [InlineData("中文", "zh-Hans", "zh")]
    [InlineData("mandarin", "zh-Hans", "zh")]
    [InlineData("English", "en", "en")]
    [InlineData("英语", "en", "en")]
    [InlineData("英文", "en", "en")]
    [InlineData("  english ", "en", "en")]
    [InlineData("Japanese", "ja", "ja")]
    [InlineData("日语", "ja", "ja")]
    [InlineData("Korean", "ko", "ko")]
    [InlineData("韩语", "ko", "ko")]
    [InlineData("Russian", "ru", "ru")]
    [InlineData("俄语", "ru", "ru")]
    [InlineData("French", "fr", "fr")]
    [InlineData("法语", "fr", "fr")]
    [InlineData("German", "de", "de")]
    [InlineData("德语", "de", "de")]
    [InlineData("Spanish", "es", "es")]
    [InlineData("西班牙语", "es", "es")]
    public void Names_resolve_through_the_same_aliases_the_display_names_use(
        string name, string bing, string tencent)
    {
        var language = FreeEngineLanguages.Find(name);

        Assert.NotNull(language);
        Assert.Equal(bing, language!.Bing);
        Assert.Equal(tencent, language.Tencent);
    }

    [Theory]
    [InlineData("service.target-language")]
    [InlineData("service.source-language")]
    public void Every_choice_of_a_language_setting_has_a_code_on_both_engines(string itemId)
    {
        // 不变量：设置里能选的每一种语言，两家引擎都有码——否则用户选了一种
        // 语言，免费引擎却只能报"不支持"。「自动检测」不是语言，由请求里的
        // null 表达。
        var item = SettingsSchema.Find(itemId)!;

        foreach (var choice in item.ChoiceList.Where(choice => choice != "自动检测"))
        {
            var language = FreeEngineLanguages.Find(choice);

            Assert.True(language is not null, $"{itemId} 的选项「{choice}」在码表里没有对应项");
            Assert.False(string.IsNullOrWhiteSpace(language!.Bing), $"{choice} 缺必应码");
            Assert.False(string.IsNullOrWhiteSpace(language.Tencent), $"{choice} 缺腾讯码");
        }
    }

    [Fact]
    public void The_two_code_tables_are_exactly_what_the_probe_verified()
    {
        // 码值独立于实现：必应用 zh-Hans，腾讯用 zh；其余六种两家同码。
        var expected = new (string Name, string Bing, string Tencent)[]
        {
            ("中文", "zh-Hans", "zh"),
            ("英语", "en", "en"),
            ("日语", "ja", "ja"),
            ("韩语", "ko", "ko"),
            ("俄语", "ru", "ru"),
            ("法语", "fr", "fr"),
            ("德语", "de", "de"),
            ("西班牙语", "es", "es"),
        };

        foreach (var (name, bing, tencent) in expected)
        {
            var language = FreeEngineLanguages.Find(name);
            Assert.Equal(bing, language?.Bing);
            Assert.Equal(tencent, language?.Tencent);
        }
    }

    [Theory]
    [InlineData("Klingon")]
    [InlineData("Italian")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("自动检测")]
    public void Names_the_table_does_not_know_do_not_resolve(string? name)
        => Assert.Null(FreeEngineLanguages.Find(name));
}

/// <summary>共用切片器（票 41）：免费引擎与公共通道吐字走同一个函数，不各抄一份。</summary>
public class StreamingSlicerTests
{
    [Fact]
    public void Pieces_rebuild_the_text_exactly_and_there_are_several_of_them()
    {
        const string text = "Hello there. How are you? 我很好，谢谢。\n第二行没有句号";

        var pieces = StreamingSlicer.Split(text);

        Assert.True(pieces.Count >= 4, $"expected several pieces, got {pieces.Count}");
        Assert.Equal(text, string.Concat(pieces));
    }

    [Fact]
    public void Empty_text_has_no_pieces()
        => Assert.Empty(StreamingSlicer.Split(string.Empty));

    [Fact]
    public void A_stretch_without_punctuation_is_capped()
    {
        var text = new string('好', 1000);

        var pieces = StreamingSlicer.Split(text);

        Assert.All(pieces, piece => Assert.True(piece.Length <= StreamingSlicer.MaxPieceChars));
        Assert.Equal(text, string.Concat(pieces));
    }

    [Fact]
    public void The_relay_backend_still_cuts_with_the_very_same_function()
    {
        // 抽出之后公共通道照旧：它留下的两个成员只是转发，不是第二份实现。
        const string text = "第一句。Second sentence! 第三句？\n尾行";

        Assert.Equal(StreamingSlicer.Split(text), RelayBackend.SplitForStreaming(text));
        Assert.Equal(StreamingSlicer.MaxPieceChars, RelayBackend.MaxPieceChars);
    }
}
