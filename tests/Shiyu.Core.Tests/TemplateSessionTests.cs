using System.Runtime.CompilerServices;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 输出清洗按类别走（票 42）：翻译类全量——剥"译文："标签、剥尾部"Note:"段、剥包裹
/// 引号；改写类只剥首尾的 &lt;text&gt;/&lt;/text&gt;。优化后的提示词里，"Notes:"
/// 可能就是正文。
/// </summary>
public class TemplateCleanupTests
{
    public static TheoryData<PromptTemplate> StyledTiers => new()
    {
        PromptTemplates.Colloquial,
        PromptTemplates.Formal,
    };

    private static readonly PromptTemplate UsersOwn =
        new("mine", "润色", PromptTemplateKind.Rewrite, "Polish it.");

    // --- 改写类 ------------------------------------------------------------------

    [Fact]
    public void A_rewrite_keeps_a_notes_section_because_it_may_be_the_prompt_itself()
    {
        const string output = "Fix the login bug.\n\nNotes:\n- keep the public API unchanged";

        Assert.Equal(output, TranslationCleanup.Clean(output, PromptTemplates.PromptOptimize));
    }

    [Theory]
    [InlineData("Fix it.\n\nNote: do not touch the schema.")]
    [InlineData("Fix it.\n\n注：不要改表结构。")]
    [InlineData("Fix it.\n\nExplanation: the bug is in login.ts.")]
    public void A_rewrite_never_loses_a_trailing_note_like_section(string output)
        => Assert.Equal(output, TranslationCleanup.Clean(output, UsersOwn));

    [Fact]
    public void A_rewrite_strips_the_text_wrapper_the_model_echoed_back()
        => Assert.Equal(
            "Write a script.",
            TranslationCleanup.Clean("<text>\nWrite a script.\n</text>", PromptTemplates.PromptOptimize));

    [Fact]
    public void A_wrapper_that_is_still_arriving_is_stripped_as_far_as_it_has_come()
    {
        // 流式中途：开标签已到、闭标签还没来；只剩闭标签的尾巴同理。
        Assert.Equal("Write a", TranslationCleanup.Clean("<text>\nWrite a", UsersOwn));
        Assert.Equal("Write a script.", TranslationCleanup.Clean("Write a script.\n</text>", UsersOwn));
    }

    [Fact]
    public void A_text_literal_inside_the_body_is_left_alone_because_the_original_may_be_xml()
    {
        const string inside = "Parse every <text> node in the XML and print </text> closers.";

        Assert.Equal(inside, TranslationCleanup.Clean(inside, UsersOwn));
        Assert.Equal(
            "Parse <text>x</text> here",
            TranslationCleanup.Clean("<text>\nParse <text>x</text> here\n</text>", UsersOwn));
    }

    [Fact]
    public void A_rewrite_keeps_labels_and_quotes_that_a_translation_would_lose()
    {
        // 翻译类的这两样清洗，对改写类可能就是正文。
        Assert.Equal("\"Quoted\"", TranslationCleanup.Clean("\"Quoted\"", UsersOwn));
        Assert.Equal("Translation: keep me", TranslationCleanup.Clean("Translation: keep me", UsersOwn));
        Assert.Equal("译文：保留", TranslationCleanup.Clean("译文：保留", UsersOwn));
    }

    [Fact]
    public void A_rewrite_is_trimmed_at_both_ends_and_nothing_in_gives_nothing_out()
    {
        Assert.Equal("Write a script.", TranslationCleanup.Clean("\n\n  Write a script.  \n", UsersOwn));
        Assert.Equal(string.Empty, TranslationCleanup.Clean("   ", UsersOwn));
    }

    [Fact]
    public void The_wrapper_tag_is_recognised_in_any_case()
        => Assert.Equal("Do it.", TranslationCleanup.Clean("<TEXT>\nDo it.\n</Text>", UsersOwn));

    // --- 翻译类 ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(StyledTiers))]
    public void A_styled_translation_loses_the_wrapper_the_label_the_note_and_the_quotes(PromptTemplate template)
    {
        Assert.Equal("你好", TranslationCleanup.Clean("<text>\n你好\n</text>", template));
        Assert.Equal("你好", TranslationCleanup.Clean("<text>\n译文：你好\n</text>", template));
        Assert.Equal("你好", TranslationCleanup.Clean("<text>你好</text>\nNote: informal.", template));
        Assert.Equal("你好", TranslationCleanup.Clean("Translation: <text>你好</text>", template));
        Assert.Equal("你好", TranslationCleanup.Clean("\"你好\"", template));
        Assert.Equal("你好", TranslationCleanup.Clean("你好\n注：这是非正式的说法。", template));
    }

    [Theory]
    [MemberData(nameof(StyledTiers))]
    public void A_styled_translation_that_needs_no_cleaning_passes_through_untouched(PromptTemplate template)
        => Assert.Equal("很高兴见到你", TranslationCleanup.Clean("很高兴见到你", template));

    [Fact]
    public void The_standard_template_cleans_exactly_as_it_did_before_templates()
    {
        // 标准档的输入不包裹，清洗也一字不改：原文本身是 XML 时，译文里的
        // <text> 是正文，不是包装。
        string[] samples =
        [
            "Translation: 你好",
            "译文：你好",
            "你好\nNote: this is an informal greeting.",
            "\"你好\"",
            "他说“你好”，然后走了",
            "<text>你好</text>",
            "  \n ",
        ];

        foreach (var sample in samples)
        {
            Assert.Equal(
                TranslationCleanup.Clean(sample),
                TranslationCleanup.Clean(sample, PromptTemplates.Standard));
        }

        Assert.Equal("<text>你好</text>", TranslationCleanup.Clean("<text>你好</text>", PromptTemplates.Standard));
    }
}

/// <summary>
/// 会话按请求里的模板行事（票 42）：回声换向重试只对翻译类生效；清洗用屏幕上那条
/// 请求的模板。
/// </summary>
public class TemplateSessionTests
{
    public static TheoryData<PromptTemplate> TranslationTemplates => new()
    {
        PromptTemplates.Standard,
        PromptTemplates.Colloquial,
        PromptTemplates.Formal,
    };

    /// <summary>每次调用吐一组片段，并记下收到的每一条请求。</summary>
    private sealed class ScriptedBackend(params string[]?[] calls) : ITranslationBackend
    {
        private readonly Queue<string[]?> _calls = new(calls);

        public List<TranslationRequest> Requests { get; } = [];

        public async IAsyncEnumerable<string> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            Requests.Add(request);
            foreach (var piece in _calls.Dequeue() ?? [])
            {
                yield return piece;
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task A_rewrite_that_comes_back_like_the_original_is_not_retried()
    {
        // 改写结果与原文相近是正常的——比如优化一段本来就写得不错的提示词。
        // 脚本后端原样回文：状态为 Finished，只调用一次。
        var backend = new ScriptedBackend(["你好世界"]);
        var session = new TranslationSession(backend);

        await session.RunAsync(
            new TranslationRequest("你好世界", "Chinese") { Template = PromptTemplates.PromptOptimize });

        Assert.Equal(TranslationState.Finished, session.State);
        Assert.Equal("你好世界", session.Text);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task A_users_own_rewrite_template_is_never_retried_either()
    {
        var template = new PromptTemplate("mine", "润色", PromptTemplateKind.Rewrite, "Polish it.");
        var backend = new ScriptedBackend(["Hello there"]);
        var session = new TranslationSession(backend);

        await session.RunAsync(new TranslationRequest("Hello there", "English") { Template = template });

        Assert.Equal(TranslationState.Finished, session.State);
        Assert.Single(backend.Requests);
    }

    [Theory]
    [MemberData(nameof(TranslationTemplates))]
    public async Task A_translation_template_still_retries_an_echo_and_keeps_its_template(PromptTemplate template)
    {
        var backend = new ScriptedBackend(["你好世界"], ["Hello world"]);
        var session = new TranslationSession(backend);

        await session.RunAsync(new TranslationRequest("你好世界", "Chinese") { Template = template });

        Assert.Equal(TranslationState.Finished, session.State);
        Assert.Equal("Hello world", session.Text);
        Assert.Equal(2, backend.Requests.Count);

        // 换向后重跑的请求照常带着模板（口语 0.3、标准 0.2 也随之保留）。
        var retry = backend.Requests[1];
        Assert.Equal(template, retry.Template);
        Assert.Equal("Chinese", retry.SourceLanguage);
        Assert.Equal("English", retry.TargetLanguage);
        Assert.Equal(template.Temperature, retry.Temperature);
        Assert.Equal(retry, session.CurrentRequest);
    }

    [Fact]
    public async Task A_rewrite_on_screen_keeps_its_notes_section()
    {
        var session = new TranslationSession(new ScriptedBackend(["Write a script.", "\n\nNotes:\n- stream it"]));

        await session.RunAsync(
            new TranslationRequest("写个脚本", "English") { Template = PromptTemplates.PromptOptimize });

        Assert.Equal("Write a script.\n\nNotes:\n- stream it", session.Text);
    }

    [Fact]
    public async Task A_rewrite_streams_without_showing_the_echoed_wrapper_tags()
    {
        var session = new TranslationSession(new ScriptedBackend(["<text>\n", "Write a ", "script.", "\n</text>"]));
        var seen = new List<string>();
        session.Updated += () => seen.Add(session.Text);

        await session.RunAsync(
            new TranslationRequest("写个脚本", "English") { Template = PromptTemplates.PromptOptimize });

        Assert.Equal("Write a script.", session.Text);
        Assert.All(seen, text => Assert.DoesNotContain("<text>", text));
    }

    [Fact]
    public async Task A_styled_translation_on_screen_is_cleaned_in_full()
    {
        var session = new TranslationSession(
            new ScriptedBackend(["<text>\n", "Translation: ", "你好", "\n</text>", "\nNote: informal."]));

        await session.RunAsync(
            new TranslationRequest("Hello there", "Chinese") { Template = PromptTemplates.Colloquial });

        Assert.Equal("你好", session.Text);
    }

    [Fact]
    public async Task The_next_run_is_cleaned_by_its_own_template()
    {
        // 面板复用 TranslationSession 的地方是每次新建，但清洗只认"屏幕上那条请求"的
        // 模板：同一个会话换个模板再跑，清洗跟着换。
        var session = new TranslationSession(
            new ScriptedBackend(["Fix it.\n\nNotes:\n- keep the API"], ["你好\nNote: informal."]));

        await session.RunAsync(
            new TranslationRequest("fix it", "English") { Template = PromptTemplates.PromptOptimize });
        Assert.Equal("Fix it.\n\nNotes:\n- keep the API", session.Text);

        await session.RunAsync(new TranslationRequest("Hello there", "Chinese"));
        Assert.Equal("你好", session.Text);
    }

    [Fact]
    public void Before_anything_has_run_the_text_is_empty()
        => Assert.Equal(string.Empty, new TranslationSession(new ScriptedBackend()).Text);
}
