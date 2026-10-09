using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 反向输入框的会话状态机（票 43，Xtranslate 的防抖状态机照拾语纪律重写）：
/// 手停 300ms 自动翻译、序号守卫、相同请求去重、翻译类的 wantCommit、改写类"按 Enter 才跑、
/// 跑完停下等第二次 Enter"。全部由 <see cref="TimeProvider"/> 驱动，测试用 <see cref="TestClock"/>
/// 拨时间，没有真实计时器、没有网络。
/// </summary>
public class ReverseInputSessionTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private static (ReverseInputSession Session, TestClock Clock) Open(
        PromptTemplate? template = null, bool templatesApply = true)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var session = new ReverseInputSession(clock);
        session.Open(template ?? PromptTemplates.Standard, templatesApply);
        return (session, clock);
    }

    /// <summary>打字、等够防抖、让它开跑；返回那一次运行。</summary>
    private static ReverseRun TypeAndRun(ReverseInputSession session, TestClock clock, string text)
    {
        session.TextChanged(text);
        clock.Advance(Debounce);
        return Assert.IsType<ReverseRun>(session.Tick().Run);
    }

    private static void Finish(ReverseInputSession session, ReverseRun run, string output)
        => session.Completed(run.Seq, output);

    // --- 开窗 --------------------------------------------------------------------------

    [Fact]
    public void A_fresh_session_is_empty_and_runs_nothing()
    {
        var (session, clock) = Open();

        Assert.Equal(ReverseInputStatus.Empty, session.Status);
        Assert.Equal(string.Empty, session.Output);
        Assert.False(session.Running);
        Assert.Null(session.TimeUntilTick());

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(default, session.Tick());
    }

    [Fact]
    public void Opening_again_starts_over_and_a_late_answer_from_the_last_visit_is_ignored()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");

        session.Open(PromptTemplates.Standard, templatesApply: true);

        Assert.Equal(string.Empty, session.Text);
        Assert.Equal(ReverseInputStatus.Empty, session.Status);
        Assert.Equal(default, session.Completed(run.Seq, "Hello"));
        Assert.Equal(string.Empty, session.Output);
    }

    [Fact]
    public void Opening_cancels_whatever_was_still_running()
    {
        var (session, clock) = Open();
        TypeAndRun(session, clock, "你好");

        Assert.True(session.Open(PromptTemplates.Standard, templatesApply: true).CancelRun);
    }

    [Fact]
    public void Opening_resets_the_direction_to_automatic_and_the_rewrite_output_to_english()
    {
        var (session, _) = Open();
        session.CycleDirection();
        Assert.Equal(ReverseDirectionChoice.ChineseToEnglish, session.Direction);

        session.Open(PromptTemplates.Standard, templatesApply: true);

        Assert.Equal(ReverseDirectionChoice.Auto, session.Direction);
        Assert.Equal(ReverseOutputLanguage.English, session.OutputLanguage);
    }

    // --- 翻译类：手停 300ms ------------------------------------------------------------

    [Fact]
    public void Nothing_runs_before_the_hand_has_been_still_for_300_milliseconds()
    {
        var (session, clock) = Open();

        session.TextChanged("你好");
        Assert.Equal(Debounce, session.TimeUntilTick());

        clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(default, session.Tick());
        Assert.Equal(TimeSpan.FromMilliseconds(1), session.TimeUntilTick());

        clock.Advance(TimeSpan.FromMilliseconds(1));
        var step = session.Tick();

        Assert.NotNull(step.Run);
        Assert.Equal(ReverseInputStatus.Running, session.Status);
        Assert.Null(session.TimeUntilTick());
    }

    [Fact]
    public void Every_keystroke_restarts_the_wait()
    {
        var (session, clock) = Open();

        session.TextChanged("你");
        clock.Advance(TimeSpan.FromMilliseconds(200));
        session.TextChanged("你好");
        clock.Advance(TimeSpan.FromMilliseconds(200));

        // 距离最后一次输入只过了 200ms。
        Assert.Equal(default, session.Tick());

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal("你好", session.Tick().Run!.Request.Text);
    }

    [Fact]
    public void The_run_asks_for_the_direction_the_text_resolves_to()
    {
        var (session, clock) = Open();

        var chinese = TypeAndRun(session, clock, "你好");
        Assert.Equal("Chinese", chinese.Request.SourceLanguage);
        Assert.Equal("English", chinese.Request.TargetLanguage);
        Assert.Equal("你好", chinese.Request.Text);

        var english = TypeAndRun(session, clock, "Thanks a lot");
        Assert.Equal("English", english.Request.SourceLanguage);
        Assert.Equal("Chinese", english.Request.TargetLanguage);
    }

    [Fact]
    public void Chinese_with_english_terms_is_asked_chinese_to_english()
    {
        var (session, clock) = Open();

        var run = TypeAndRun(session, clock, "帮我 fix 这个 bug in login.ts");

        Assert.Equal("Chinese", run.Request.SourceLanguage);
        Assert.Equal("English", run.Request.TargetLanguage);
    }

    [Fact]
    public void The_text_sent_is_trimmed_but_what_the_user_typed_is_left_alone()
    {
        var (session, clock) = Open();

        var run = TypeAndRun(session, clock, "  你好 \n");

        Assert.Equal("你好", run.Request.Text);
        Assert.Equal("  你好 \n", session.Text);
    }

    [Fact]
    public void The_run_uses_the_template_the_session_was_opened_with()
    {
        var (session, clock) = Open(PromptTemplates.Colloquial);

        Assert.Same(PromptTemplates.Colloquial, TypeAndRun(session, clock, "你好").Request.Template);
    }

    [Fact]
    public void Blank_input_never_runs_and_clears_the_output()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        Finish(session, run, "Hello");

        session.TextChanged("   ");

        Assert.Equal(ReverseInputStatus.Empty, session.Status);
        Assert.Equal(string.Empty, session.Output);
        Assert.Null(session.TimeUntilTick());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(default, session.Tick());
    }

    [Fact]
    public void Clearing_the_input_cancels_the_run_in_flight()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");

        var step = session.TextChanged(string.Empty);

        Assert.True(step.CancelRun);
        Assert.Equal(default, session.Completed(run.Seq, "Hello"));
        Assert.Equal(string.Empty, session.Output);
    }

    // --- 输入法组字 ----------------------------------------------------------------------

    [Fact]
    public void While_the_ime_is_composing_the_wait_does_not_run_out()
    {
        // WPF 的 TextBox.Text 含着组字中的拼音："nihao" 还没选字就被译了，既白费又闪一下乱码。
        // 用户盯着候选词看的那几百毫秒，不算"手停了"。
        var (session, clock) = Open();
        session.SetComposing(true);
        session.TextChanged("nihao");

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(default, session.Tick());
        Assert.False(session.Running);
    }

    [Fact]
    public void When_the_composition_ends_the_wait_starts_over_on_the_committed_text()
    {
        var (session, clock) = Open();
        session.SetComposing(true);
        session.TextChanged("nihao");
        clock.Advance(TimeSpan.FromSeconds(2));
        session.Tick();

        // 选字上屏：文字变成"你好"，组字结束。重新计 300ms。
        session.SetComposing(false);
        session.TextChanged("你好");

        Assert.Equal(Debounce, session.TimeUntilTick());
        clock.Advance(Debounce);
        Assert.Equal("你好", session.Tick().Run!.Request.Text);
    }

    [Fact]
    public void A_composition_that_ends_without_changing_the_text_still_re_arms_the_wait()
    {
        // 组字被取消（Esc）回到原样，没有最后一次 TextChanged：结束时自己重新计时。
        var (session, clock) = Open();
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");
        session.TextChanged("你好吗");
        session.SetComposing(true);
        clock.Advance(TimeSpan.FromSeconds(1));
        session.Tick();

        session.SetComposing(false);

        Assert.Equal(Debounce, session.TimeUntilTick());
    }

    [Fact]
    public void Composing_makes_no_difference_to_a_rewrite_which_never_runs_by_itself()
    {
        var (session, clock) = Open(PromptTemplates.PromptOptimize);
        session.SetComposing(true);
        session.TextChanged("xiegejiaoben");

        session.SetComposing(false);

        Assert.Null(session.TimeUntilTick());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(default, session.Tick());
    }

    [Fact]
    public void Opening_again_forgets_a_composition_that_was_cut_short()
    {
        var (session, clock) = Open();
        session.SetComposing(true);

        session.Open(PromptTemplates.Standard, templatesApply: true);
        session.TextChanged("你好");
        clock.Advance(Debounce);

        Assert.NotNull(session.Tick().Run);
    }

    // --- 去重 --------------------------------------------------------------------------

    [Fact]
    public void The_same_request_is_not_sent_twice()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        Finish(session, run, "Hello");

        // 只差首尾空白：同一个请求。
        session.TextChanged("你好 ");

        Assert.Null(session.TimeUntilTick());
        clock.Advance(Debounce);
        Assert.Equal(default, session.Tick());
        Assert.Equal(ReverseInputStatus.Ready, session.Status);
    }

    [Fact]
    public void Typing_something_and_deleting_it_again_does_not_ask_again()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        Finish(session, run, "Hello");

        session.TextChanged("你好吗");
        Assert.Equal(ReverseInputStatus.Debouncing, session.Status);

        session.TextChanged("你好");

        Assert.Equal(ReverseInputStatus.Ready, session.Status);
        clock.Advance(Debounce);
        Assert.Equal(default, session.Tick());
        Assert.Equal("Hello", session.Output);
        Assert.False(session.OutputFaded);
    }

    [Fact]
    public void A_different_text_is_a_different_request()
    {
        var (session, clock) = Open();
        var first = TypeAndRun(session, clock, "你好");
        Finish(session, first, "Hello");

        var second = TypeAndRun(session, clock, "你好吗");

        Assert.NotEqual(first.Seq, second.Seq);
        Assert.Equal("你好吗", second.Request.Text);
    }

    [Fact]
    public void Coming_back_to_a_text_whose_run_was_cancelled_asks_again()
    {
        var (session, clock) = Open();
        TypeAndRun(session, clock, "你好");

        // 打一个字就把在途的请求作废了（新的输入取消在途请求）；删掉它回到原文，
        // 那次请求已经没有了，要重新发。
        var typed = session.TextChanged("你好吗");
        Assert.True(typed.CancelRun);

        session.TextChanged("你好");
        clock.Advance(Debounce);
        Assert.NotNull(session.Tick().Run);
    }

    [Fact]
    public void Typing_the_same_text_again_while_it_runs_neither_cancels_nor_re_sends()
    {
        var (session, clock) = Open();
        TypeAndRun(session, clock, "你好");

        var step = session.TextChanged("你好 ");

        Assert.Equal(default, step);
        Assert.Null(session.TimeUntilTick());
        Assert.True(session.Running);
    }

    [Fact]
    public void A_failed_request_is_asked_again_when_the_same_text_comes_back()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Failed(run.Seq, new TranslationFailedException("boom"), "boom");

        session.TextChanged("你好 ");
        clock.Advance(Debounce);

        // 去重只管"无错"的请求：失败过的，重新来一次是应该的。
        Assert.NotNull(session.Tick().Run);
    }

    // --- 序号与取消 ---------------------------------------------------------------------

    [Fact]
    public void New_input_cancels_the_run_in_flight()
    {
        var (session, clock) = Open();
        TypeAndRun(session, clock, "你好");

        var step = session.TextChanged("你好吗");

        Assert.True(step.CancelRun);
        Assert.False(session.Running);
    }

    [Fact]
    public void A_late_answer_from_a_cancelled_run_is_ignored()
    {
        var (session, clock) = Open();
        var stale = TypeAndRun(session, clock, "你好");
        session.TextChanged("你好吗");

        Assert.Equal(default, session.Partial(stale.Seq, "Hel"));
        Assert.Equal(default, session.Completed(stale.Seq, "Hello"));

        Assert.NotEqual("Hello", session.Output);
        Assert.Equal(ReverseInputStatus.Debouncing, session.Status);
    }

    [Fact]
    public void Sequence_numbers_only_go_up()
    {
        var (session, clock) = Open();

        var seqs = new List<int>();
        foreach (var text in new[] { "你好", "你好吗", "你好吗朋友" })
        {
            seqs.Add(TypeAndRun(session, clock, text).Seq);
        }

        Assert.Equal(seqs.OrderBy(seq => seq), seqs);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
    }

    [Fact]
    public void A_newer_run_wins_even_when_the_older_one_answers_afterwards()
    {
        var (session, clock) = Open();
        var older = TypeAndRun(session, clock, "你好");
        var newer = TypeAndRun(session, clock, "你好吗");

        session.Completed(newer.Seq, "How are you?");
        session.Completed(older.Seq, "Hello");

        Assert.Equal("How are you?", session.Output);
    }

    // --- 输出的显示 ---------------------------------------------------------------------

    [Fact]
    public void Streaming_text_replaces_the_output_with_the_cursor_on()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");

        session.Partial(run.Seq, "Hel");

        Assert.Equal("Hel", session.Output);
        Assert.False(session.OutputFaded);
        Assert.True(session.Running);

        session.Partial(run.Seq, "Hello");
        session.Completed(run.Seq, "Hello");

        Assert.Equal("Hello", session.Output);
        Assert.False(session.Running);
        Assert.Equal(ReverseInputStatus.Ready, session.Status);
    }

    [Fact]
    public void While_waiting_the_old_output_stays_but_faded_and_the_cursor_shows()
    {
        var (session, clock) = Open();
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");

        // 改了输入：旧输出已经过期，淡显，但还在。
        session.TextChanged("你好吗");
        Assert.Equal("Hello", session.Output);
        Assert.True(session.OutputFaded);
        Assert.False(session.Running);

        // 开跑了、首字还没到：旧输出继续淡显，尾部挂光标。
        clock.Advance(Debounce);
        var run = session.Tick().Run!;
        Assert.Equal("Hello", session.Output);
        Assert.True(session.OutputFaded);
        Assert.True(session.Running);

        // 首字到了：换成新的，不再淡。
        session.Partial(run.Seq, "How");
        Assert.Equal("How", session.Output);
        Assert.False(session.OutputFaded);
    }

    [Fact]
    public void An_empty_piece_does_not_wipe_the_output()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Partial(run.Seq, "Hel");

        session.Partial(run.Seq, string.Empty);

        Assert.Equal("Hel", session.Output);
    }

    [Fact]
    public void A_cancelled_runs_partial_text_stays_as_the_faded_old_output()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Partial(run.Seq, "Hel");

        session.TextChanged("你好吗");

        Assert.Equal("Hel", session.Output);
        Assert.True(session.OutputFaded);
    }

    // --- Enter：翻译类 -------------------------------------------------------------------

    [Fact]
    public void Enter_pastes_when_the_output_already_matches_the_input()
    {
        var (session, clock) = Open();
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");

        var step = session.Enter();

        Assert.Equal("Hello", step.Paste);
        Assert.Null(step.Run);
    }

    [Fact]
    public void Enter_before_the_wait_is_over_runs_at_once_and_pastes_when_the_answer_arrives()
    {
        var (session, clock) = Open();
        session.TextChanged("你好");
        clock.Advance(TimeSpan.FromMilliseconds(50));

        var step = session.Enter();

        // 不必等满 300ms：用户已经说了"就这样"。
        var run = Assert.IsType<ReverseRun>(step.Run);
        Assert.True(session.WantCommit);
        Assert.Null(step.Paste);

        // 之后的防抖不会再发一遍。
        clock.Advance(Debounce);
        Assert.Equal(default, session.Tick());

        var arrived = session.Completed(run.Seq, "Hello");
        Assert.Equal("Hello", arrived.Paste);
        Assert.False(session.WantCommit);
    }

    [Fact]
    public void Enter_while_the_request_is_running_only_marks_the_commit()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");

        var step = session.Enter();

        Assert.Equal(default, step);
        Assert.True(session.WantCommit);
        Assert.Equal("Hello", session.Completed(run.Seq, "Hello").Paste);
    }

    [Fact]
    public void Typing_again_takes_back_a_pending_commit()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Enter();

        session.TextChanged("你好吗");

        Assert.False(session.WantCommit);
        Assert.Equal(default, session.Completed(run.Seq, "Hello"));
    }

    [Fact]
    public void A_failure_clears_the_pending_commit_and_pastes_nothing()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Enter();

        var step = session.Failed(run.Seq, new TranslationFailedException("服务响应超时。"), "服务响应超时。");

        Assert.Equal(default, step);
        Assert.False(session.WantCommit);
        Assert.Equal(ReverseInputStatus.Failed, session.Status);
        Assert.Equal("服务响应超时。", session.FailureMessage);
        Assert.IsType<TranslationFailedException>(session.Failure);
        Assert.Equal(string.Empty, session.Output);
    }

    [Fact]
    public void Enter_after_a_failure_tries_again_and_pastes_on_arrival()
    {
        var (session, clock) = Open();
        var first = TypeAndRun(session, clock, "你好");
        session.Failed(first.Seq, new TranslationFailedException("boom"), "boom");

        var step = session.Enter();

        var retry = Assert.IsType<ReverseRun>(step.Run);
        Assert.NotEqual(first.Seq, retry.Seq);
        Assert.Equal("Hello", session.Completed(retry.Seq, "Hello").Paste);
    }

    [Fact]
    public void Enter_on_blank_input_does_nothing()
    {
        var (session, _) = Open();

        Assert.Equal(default, session.Enter());
        session.TextChanged("  \n ");
        Assert.Equal(default, session.Enter());
        Assert.False(session.WantCommit);
    }

    [Fact]
    public void An_empty_translation_is_a_failure_not_something_to_paste()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Enter();

        var step = session.Completed(run.Seq, "  \n");

        Assert.Null(step.Paste);
        Assert.Equal(ReverseInputStatus.Failed, session.Status);
        Assert.False(string.IsNullOrWhiteSpace(session.FailureMessage));
    }

    [Fact]
    public void A_stale_output_is_never_what_Enter_pastes()
    {
        var (session, clock) = Open();
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");
        session.TextChanged("你好吗");

        var step = session.Enter();

        Assert.Null(step.Paste);
        Assert.NotNull(step.Run);
    }

    // --- 改写类：按 Enter 才跑，跑完停下 ---------------------------------------------------

    [Fact]
    public void A_rewrite_template_never_runs_by_itself()
    {
        var (session, clock) = Open(PromptTemplates.PromptOptimize);

        session.TextChanged("帮我写一个把 CSV 转成 JSON 的脚本");

        Assert.Equal(ReverseInputStatus.NeedsEnter, session.Status);
        Assert.Null(session.TimeUntilTick());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(default, session.Tick());
    }

    [Fact]
    public void Enter_starts_a_rewrite_and_it_stops_for_review_when_it_finishes()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("帮我写一个脚本");

        var step = session.Enter();
        var run = Assert.IsType<ReverseRun>(step.Run);
        Assert.False(session.WantCommit);

        var done = session.Completed(run.Seq, "Write a script.");

        // 跑完停下：整理出来的提示词要先过目再贴。
        Assert.Null(done.Paste);
        Assert.Equal(ReverseInputStatus.Ready, session.Status);
        Assert.Equal("Write a script.", session.Output);
    }

    [Fact]
    public void The_second_Enter_pastes_the_reviewed_rewrite()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("帮我写一个脚本");
        var run = session.Enter().Run!;
        session.Completed(run.Seq, "Write a script.");

        var step = session.Enter();

        Assert.Equal("Write a script.", step.Paste);
        Assert.Null(step.Run);
    }

    [Fact]
    public void Enter_while_a_rewrite_runs_neither_starts_another_nor_asks_for_a_commit()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("帮我写一个脚本");
        var run = session.Enter().Run!;

        var again = session.Enter();

        Assert.Equal(default, again);
        Assert.False(session.WantCommit);
        Assert.Null(session.Completed(run.Seq, "Write a script.").Paste);
    }

    [Fact]
    public void Editing_the_input_after_a_rewrite_makes_it_stale_and_needs_Enter_again()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("帮我写一个脚本");
        session.Completed(session.Enter().Run!.Seq, "Write a script.");

        session.TextChanged("帮我写一个脚本，要流式处理");

        Assert.Equal(ReverseInputStatus.NeedsEnter, session.Status);
        Assert.True(session.OutputFaded);
        var step = session.Enter();
        Assert.Null(step.Paste);
        Assert.NotNull(step.Run);
    }

    [Fact]
    public void Editing_while_a_rewrite_runs_cancels_it_and_waits_for_Enter()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("帮我写一个脚本");
        var run = session.Enter().Run!;

        var step = session.TextChanged("帮我写两个脚本");

        Assert.True(step.CancelRun);
        Assert.Equal(default, session.Completed(run.Seq, "Write a script."));
        Assert.Equal(ReverseInputStatus.NeedsEnter, session.Status);
    }

    [Fact]
    public void A_rewrite_defaults_to_english_output_whatever_language_the_input_is_in()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);

        // 反向输入框是"往外写"的地方：输入碰巧是英文，也不能因此输出中文。
        session.TextChanged("write a script that converts csv to json");
        var request = session.Enter().Run!.Request;

        Assert.Equal("English", request.TargetLanguage);
        Assert.Null(request.SourceLanguage);
        Assert.Same(PromptTemplates.PromptOptimize, request.Template);
    }

    [Fact]
    public void A_rewrite_can_be_asked_for_chinese_output_with_Tab()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("write a script");

        session.CycleDirection();
        var request = session.Enter().Run!.Request;

        Assert.Equal(ReverseOutputLanguage.Chinese, session.OutputLanguage);
        Assert.Equal("Chinese", request.TargetLanguage);
    }

    // --- 方向：Tab ----------------------------------------------------------------------

    [Fact]
    public void Tab_cycles_auto_then_chinese_to_english_then_english_to_chinese_then_auto()
    {
        var (session, _) = Open();

        Assert.Equal(ReverseDirectionChoice.Auto, session.Direction);
        session.CycleDirection();
        Assert.Equal(ReverseDirectionChoice.ChineseToEnglish, session.Direction);
        session.CycleDirection();
        Assert.Equal(ReverseDirectionChoice.EnglishToChinese, session.Direction);
        session.CycleDirection();
        Assert.Equal(ReverseDirectionChoice.Auto, session.Direction);
    }

    [Fact]
    public void The_direction_chip_reads_what_automatic_resolved_to_once_there_is_text()
    {
        var (session, _) = Open();
        Assert.Equal("自动", session.DirectionLabel);

        session.TextChanged("你好");
        Assert.Equal("自动 · 中→英", session.DirectionLabel);

        session.TextChanged("Thanks");
        Assert.Equal("自动 · 英→中", session.DirectionLabel);

        session.CycleDirection();
        Assert.Equal("中→英", session.DirectionLabel);
        session.CycleDirection();
        Assert.Equal("英→中", session.DirectionLabel);
    }

    [Fact]
    public void Switching_direction_re_runs_a_translation_at_once_without_waiting()
    {
        var (session, clock) = Open();
        Finish(session, TypeAndRun(session, clock, "Thanks a lot"), "非常感谢");

        var step = session.CycleDirection();

        // 自动认成英→中；点一下变成显式的中→英：请求变了，立刻重跑。
        var run = Assert.IsType<ReverseRun>(step.Run);
        Assert.Equal("Chinese", run.Request.SourceLanguage);
        Assert.Equal("English", run.Request.TargetLanguage);
        Assert.Null(session.TimeUntilTick());
    }

    [Fact]
    public void Switching_to_the_direction_automatic_had_already_chosen_does_not_ask_again()
    {
        var (session, clock) = Open();
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");

        // 自动 → 显式中→英：对这句话是同一个请求。
        var step = session.CycleDirection();

        Assert.Equal(default, step);
        Assert.Equal(ReverseInputStatus.Ready, session.Status);
        Assert.Equal("Hello", session.Output);
    }

    [Fact]
    public void Switching_direction_to_a_different_request_replaces_the_run_and_takes_back_a_pending_commit()
    {
        var (session, clock) = Open();
        var old = TypeAndRun(session, clock, "你好");
        session.Enter();

        // 自动 → 显式中→英：对这句话是同一个请求，什么都没变，贴回的意愿仍然有效。
        Assert.Equal(default, session.CycleDirection());
        Assert.True(session.WantCommit);

        // 中→英 → 英→中：请求变了。旧的作废，新的立刻跑，之前那次"译完就贴"不再算数。
        var step = session.CycleDirection();

        var run = Assert.IsType<ReverseRun>(step.Run);
        Assert.NotEqual(old.Seq, run.Seq);
        Assert.False(session.WantCommit);
        Assert.Equal(default, session.Completed(old.Seq, "Hello"));
        Assert.Null(session.Completed(run.Seq, "你好").Paste);
    }

    [Fact]
    public void Switching_the_output_language_of_a_rewrite_does_not_run_it()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("写个脚本");
        session.Completed(session.Enter().Run!.Seq, "Write a script.");

        var step = session.CycleDirection();

        Assert.Null(step.Run);
        Assert.Equal(ReverseInputStatus.NeedsEnter, session.Status);
        Assert.True(session.OutputFaded);
    }

    [Fact]
    public void Switching_the_output_language_cancels_a_rewrite_that_is_running()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("写个脚本");
        var run = session.Enter().Run!;

        var step = session.CycleDirection();

        Assert.True(step.CancelRun);
        Assert.Equal(default, session.Completed(run.Seq, "Write a script."));
    }

    [Fact]
    public void Each_kind_remembers_its_own_direction_across_template_switches()
    {
        var (session, _) = Open();
        session.CycleDirection(); // 翻译类：显式中→英

        session.SelectTemplate(PromptTemplates.PromptOptimize);
        session.CycleDirection(); // 改写类：输出中文
        Assert.Equal(ReverseOutputLanguage.Chinese, session.OutputLanguage);

        session.SelectTemplate(PromptTemplates.Formal);
        Assert.Equal(ReverseDirectionChoice.ChineseToEnglish, session.Direction);
    }

    [Fact]
    public void A_rewrite_template_without_a_target_placeholder_has_no_direction_to_switch()
    {
        var mine = new PromptTemplate("mine", "润色", PromptTemplateKind.Rewrite, "Polish it.");
        var (session, _) = Open(mine);
        session.TextChanged("写个脚本");

        Assert.False(session.DirectionVisible);
        Assert.Equal(default, session.CycleDirection());
        Assert.Equal(ReverseOutputLanguage.English, session.OutputLanguage);
    }

    [Fact]
    public void A_rewrite_template_with_a_target_placeholder_shows_the_direction()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);

        Assert.True(session.DirectionVisible);
        Assert.Equal("输出英文", session.DirectionLabel);
        session.CycleDirection();
        Assert.Equal("输出中文", session.DirectionLabel);
    }

    // --- 模板：Ctrl+E 与点选 -------------------------------------------------------------

    [Fact]
    public void Ctrl_E_walks_the_shared_cycle_and_wraps_around()
    {
        var (session, _) = Open(PromptTemplates.Standard);
        var cycle = PromptTemplates.Cycle(new AppSettings());

        var seen = new List<string> { session.RuntimeTemplate.Id };
        for (var step = 0; step < cycle.Count; step++)
        {
            session.CycleTemplate(cycle);
            seen.Add(session.RuntimeTemplate.Id);
        }

        Assert.Equal(
            [
                PromptTemplate.StandardId,
                PromptTemplate.ColloquialId,
                PromptTemplate.FormalId,
                PromptTemplate.PromptOptimizeId,
                PromptTemplate.StandardId,
            ],
            seen);
    }

    [Fact]
    public void Ctrl_E_with_nothing_else_in_the_cycle_stays_put()
    {
        var (session, clock) = Open(PromptTemplates.Standard);
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");

        var step = session.CycleTemplate([PromptTemplates.Standard]);

        Assert.Equal(default, step);
        Assert.Equal("Hello", session.Output);
    }

    [Fact]
    public void A_template_switch_re_runs_a_translation_at_once_with_the_new_template()
    {
        var (session, clock) = Open(PromptTemplates.Standard);
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");

        var step = session.SelectTemplate(PromptTemplates.Colloquial);

        var run = Assert.IsType<ReverseRun>(step.Run);
        Assert.Same(PromptTemplates.Colloquial, run.Request.Template);
        Assert.Equal("你好", run.Request.Text);
    }

    [Fact]
    public void A_template_switch_takes_back_a_pending_commit()
    {
        var (session, clock) = Open(PromptTemplates.Standard);
        var run = TypeAndRun(session, clock, "你好");
        session.Enter();

        session.SelectTemplate(PromptTemplates.Formal);

        Assert.False(session.WantCommit);
        Assert.Equal(default, session.Completed(run.Seq, "Hello"));
    }

    [Fact]
    public void Switching_to_a_rewrite_template_stops_the_auto_run_and_the_old_output_goes_stale()
    {
        var (session, clock) = Open(PromptTemplates.Standard);
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");

        var step = session.SelectTemplate(PromptTemplates.PromptOptimize);

        Assert.Null(step.Run);
        Assert.Equal(ReverseInputStatus.NeedsEnter, session.Status);
        Assert.True(session.OutputFaded);
        Assert.Null(session.TimeUntilTick());
    }

    [Fact]
    public void Switching_from_a_rewrite_back_to_a_translation_runs_it_at_once()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("你好");

        var step = session.SelectTemplate(PromptTemplates.Colloquial);

        Assert.NotNull(step.Run);
        Assert.Equal(ReverseInputStatus.Running, session.Status);
    }

    [Fact]
    public void Selecting_the_template_already_in_use_does_nothing()
    {
        var (session, clock) = Open(PromptTemplates.Colloquial);
        Finish(session, TypeAndRun(session, clock, "你好"), "Hello");

        Assert.Equal(default, session.SelectTemplate(PromptTemplates.Colloquial));
        Assert.Equal(ReverseInputStatus.Ready, session.Status);
    }

    [Fact]
    public void A_template_switch_with_no_text_just_changes_the_template()
    {
        var (session, _) = Open(PromptTemplates.Standard);

        var step = session.SelectTemplate(PromptTemplates.Formal);

        Assert.Equal(default, step);
        Assert.Same(PromptTemplates.Formal, session.RuntimeTemplate);
    }

    [Fact]
    public void When_templates_do_not_apply_the_chip_hides_and_the_request_is_the_standard_one()
    {
        // 免费引擎没有 prompt：模板 chip 隐藏、按标准模板构建请求，反向输入框照样能用。
        var (session, clock) = Open(PromptTemplates.Formal, templatesApply: false);

        Assert.False(session.TemplateChipVisible);
        Assert.Same(PromptTemplates.Standard, session.Template);
        Assert.Same(PromptTemplates.Standard, TypeAndRun(session, clock, "你好").Request.Template);
        Assert.True(session.DirectionVisible);
    }

    [Fact]
    public void When_templates_do_not_apply_Ctrl_E_and_the_list_do_nothing()
    {
        var (session, _) = Open(PromptTemplates.Standard, templatesApply: false);
        session.TextChanged("你好");

        Assert.Equal(default, session.CycleTemplate(PromptTemplates.Cycle(new AppSettings())));
        Assert.Equal(default, session.SelectTemplate(PromptTemplates.PromptOptimize));
        Assert.Same(PromptTemplates.Standard, session.Template);
    }

    [Fact]
    public void A_rewrite_template_that_does_not_apply_is_treated_as_standard_and_runs_by_itself()
    {
        var (session, clock) = Open(PromptTemplates.PromptOptimize, templatesApply: false);

        session.TextChanged("你好");

        Assert.Equal(Debounce, session.TimeUntilTick());
        clock.Advance(Debounce);
        Assert.NotNull(session.Tick().Run);
    }

    [Fact]
    public void The_chips_carry_names_a_screen_reader_can_say()
    {
        var (session, _) = Open(PromptTemplates.Colloquial);
        session.TextChanged("你好");

        Assert.Contains("口语", session.TemplateChipName);
        Assert.Contains("自动 · 中→英", session.DirectionChipName);
    }

    // --- Esc 与失焦 ---------------------------------------------------------------------

    [Fact]
    public void Escape_cancels_the_run_in_flight_and_pastes_nothing()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Enter();

        var step = session.Escape();

        Assert.True(step.CancelRun);
        Assert.Null(step.Paste);
        Assert.Equal(default, session.Completed(run.Seq, "Hello"));
    }

    [Fact]
    public void Escape_with_nothing_running_has_nothing_to_cancel()
    {
        var (session, _) = Open();

        Assert.Equal(default, session.Escape());
    }

    [Fact]
    public void Losing_focus_within_the_first_300_milliseconds_is_ignored_so_it_cannot_close_itself()
    {
        var (session, clock) = Open();

        Assert.False(session.ShouldHideOnFocusLoss());
        clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.False(session.ShouldHideOnFocusLoss());

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(session.ShouldHideOnFocusLoss());
    }

    [Fact]
    public void The_grace_starts_over_with_every_visit()
    {
        var (session, clock) = Open();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(session.ShouldHideOnFocusLoss());

        session.Open(PromptTemplates.Standard, templatesApply: true);

        Assert.False(session.ShouldHideOnFocusLoss());
    }

    // --- 状态与提示 ---------------------------------------------------------------------

    [Fact]
    public void The_status_follows_the_life_of_a_translation()
    {
        var (session, clock) = Open();
        Assert.Equal(ReverseInputStatus.Empty, session.Status);

        session.TextChanged("你好");
        Assert.Equal(ReverseInputStatus.Debouncing, session.Status);

        clock.Advance(Debounce);
        var run = session.Tick().Run!;
        Assert.Equal(ReverseInputStatus.Running, session.Status);

        session.Completed(run.Seq, "Hello");
        Assert.Equal(ReverseInputStatus.Ready, session.Status);
    }

    [Fact]
    public void A_failed_attempt_stops_showing_its_error_once_the_input_changes()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Failed(run.Seq, new TranslationFailedException("boom"), "boom");
        Assert.NotNull(session.Failure);

        session.TextChanged("你好吗");

        Assert.Null(session.Failure);
        Assert.Null(session.FailureMessage);
        Assert.Equal(ReverseInputStatus.Debouncing, session.Status);
    }

    [Fact]
    public void The_hint_says_what_Enter_will_do_in_each_state()
    {
        var (session, clock) = Open();
        Assert.Contains("Esc", session.Hint);

        session.TextChanged("你好");
        Assert.Contains("Enter", session.Hint);

        clock.Advance(Debounce);
        var run = session.Tick().Run!;
        Assert.Contains("正在翻译", session.Hint);

        // wantCommit 等待期：Xtranslate 的"翻译完就上屏…"。
        session.Enter();
        Assert.Contains("译完", session.Hint);

        Assert.Equal("Hello", session.Completed(run.Seq, "Hello").Paste);
    }

    [Fact]
    public void The_rewrite_hint_asks_for_a_second_Enter_after_the_review()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("写个脚本");
        Assert.Contains("Enter", session.Hint);
        Assert.Contains("整理", session.Hint);

        var run = session.Enter().Run!;
        Assert.Contains("正在整理", session.Hint);

        session.Completed(run.Seq, "Write a script.");
        Assert.Contains("再按 Enter", session.Hint);
    }

    [Fact]
    public void The_hint_after_a_failure_offers_a_retry()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");

        session.Failed(run.Seq, null, "boom");

        Assert.Contains("重试", session.Hint);
    }

    // --- 落定的输出（翻译记录只记它，用户需求 2026-10-09） ----------------------------------

    [Fact]
    public void Only_a_finished_answer_to_the_current_text_is_settled()
    {
        var (session, clock) = Open();
        Assert.Null(session.SettledOutput);

        var run = TypeAndRun(session, clock, "你好");
        session.Partial(run.Seq, "Hel");
        Assert.Null(session.SettledOutput);

        Finish(session, run, "Hello");
        Assert.Equal("Hello", session.SettledOutput);

        // 接着打字：屏幕上淡显的旧输出不是这句话的答案。
        session.TextChanged("你好，明天见");
        Assert.True(session.OutputFaded);
        Assert.Null(session.SettledOutput);
    }

    [Fact]
    public void A_failed_or_escaped_attempt_settles_nothing()
    {
        var (session, clock) = Open();
        var run = TypeAndRun(session, clock, "你好");
        session.Failed(run.Seq, null, "boom");
        Assert.Null(session.SettledOutput);

        var retry = TypeAndRun(session, clock, "你好呀");
        Finish(session, retry, "Hi");
        session.Escape();
        Assert.Null(session.SettledOutput);
    }

    [Fact]
    public void A_finished_rewrite_is_settled_before_the_second_enter()
    {
        var (session, _) = Open(PromptTemplates.PromptOptimize);
        session.TextChanged("写个脚本");

        var run = session.Enter().Run!;
        session.Completed(run.Seq, "Write a script.");

        Assert.Equal("Write a script.", session.SettledOutput);
    }
}
