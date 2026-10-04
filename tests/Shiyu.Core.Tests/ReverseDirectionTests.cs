using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 反向输入框的方向判定（票 43）：汉字数 ×5 ≥ 拉丁字母数 → 中→英，否则英→中。
/// 它是独立的一条规则，不是 <see cref="LanguageGuess"/> 的 ×2：反向输入框里中文夹英文
/// 术语是常态，按 ×2 算会把"帮我 fix 这个 bug in login.ts"判成英文、反过来译成中文。
/// </summary>
public class ReverseDirectionTests
{
    [Theory]
    [InlineData("你好")]
    [InlineData("今天下午的会议改到明天上午十点")]
    [InlineData("这个函数为什么返回空？")]
    public void Plain_chinese_goes_chinese_to_english(string text)
        => Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect(text));

    [Theory]
    [InlineData("Hello there")]
    [InlineData("Why does this function return null?")]
    [InlineData("ok")]
    public void Plain_english_goes_english_to_chinese(string text)
        => Assert.Equal(ReversePair.EnglishToChinese, ReverseDirection.Detect(text));

    [Fact]
    public void Chinese_with_english_terms_still_goes_chinese_to_english()
    {
        // 票面点名的例子：汉字 4 个、拉丁字母 15 个。×5 → 20 ≥ 15，中→英。
        const string text = "帮我 fix 这个 bug in login.ts";

        Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect(text));
    }

    [Fact]
    public void The_same_text_is_english_by_the_panels_double_weight_which_is_why_this_rule_exists()
    {
        // 面板的方向标签（LanguageGuess，×2）与回声换向有测试钉着，不能动；
        // 这里把两条规则对同一句话的分歧钉出来，谁改了谁就得看这条。
        const string text = "帮我 fix 这个 bug in login.ts";

        Assert.Equal(TextScript.Latin, LanguageGuess.FromText(text).Script);
        Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect(text));
    }

    [Fact]
    public void The_boundary_is_inclusive_on_the_chinese_side()
    {
        // 1 个汉字 ×5 = 5：恰好等于 5 个拉丁字母时算中文，多一个字母就翻过去。
        Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect("你 abcde"));
        Assert.Equal(ReversePair.EnglishToChinese, ReverseDirection.Detect("你 abcdef"));
    }

    [Fact]
    public void The_weight_is_five()
        => Assert.Equal(5, ReverseDirection.HanWeight);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Nothing_to_judge_falls_on_the_default_out_direction(string text)
    {
        // 规则本身：0 × 5 ≥ 0，中→英。反向输入框是"往外写"的地方，默认出英文。
        // 空串不会真的发请求（会话里空输入不跑），这里只钉住判定是确定的。
        Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect(text));
    }

    [Fact]
    public void Null_is_treated_as_empty()
        => Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect(null));

    [Theory]
    [InlineData("12345 !!! ???")]
    [InlineData("😀😀")]
    public void Text_without_any_letters_counts_as_nothing_to_judge(string text)
        => Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect(text));

    [Fact]
    public void Accented_latin_letters_count_as_latin()
    {
        // café 的 é 与 a-z 同属拉丁字母；× 与 ÷ 虽落在同一码位段里，不是字母，不计。
        Assert.Equal(ReversePair.EnglishToChinese, ReverseDirection.Detect("café crème brûlée"));
        Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect("你 3×4÷2"));
    }

    [Fact]
    public void Kana_and_hangul_do_not_count_as_either_side()
    {
        // 方向只做中↔英（其他外语留作后续）：假名、谚文既不算汉字也不算拉丁。
        Assert.Equal(ReversePair.ChineseToEnglish, ReverseDirection.Detect("こんにちは"));
        Assert.Equal(ReversePair.EnglishToChinese, ReverseDirection.Detect("こんにちは hello"));
    }

    [Fact]
    public void A_pair_names_its_languages_the_way_the_prompt_expects_them()
    {
        Assert.Equal(("Chinese", "English"), ReverseDirection.Languages(ReversePair.ChineseToEnglish));
        Assert.Equal(("English", "Chinese"), ReverseDirection.Languages(ReversePair.EnglishToChinese));
    }
}
