using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

public class LogTests
{
    // Log 是全进程共享的静态门面。所有走 Attach 的用例集中在这一个类里，
    // xUnit 按类串行，互不踩脚；Detach 兜底防止漏到别的测试。
    public sealed class Facade : IDisposable
    {
        private readonly MemoryLogSink _sink = new();

        public Facade()
        {
            Log.Attach(_sink);
        }

        public void Dispose() => Log.Detach();

        /// <summary>
        /// 行的结构是"时间 级别 事件 字段…"，事件名恰是第三个词。并行跑的
        /// 别的测试类会驱动产品代码往这个进程级 sink 里也落几行（Backup、
        /// 编排器的失败路径都记日志）——断言只认本用例的事件，Ambient 静态
        /// 量在并行测试里就该这么读，否则是掷骰子。
        /// </summary>
        private string SingleLineOf(string eventName)
            => Assert.Single(_sink.Lines.Where(line =>
                line.Split(' ').Length > 2 && line.Split(' ')[2] == eventName));

        [Fact]
        public void An_event_lands_as_one_line_with_its_fields()
        {
            Log.Event(LogEvent.RetentionSwept, ("removed", 3), ("days", 30));

            var line = SingleLineOf(nameof(LogEvent.RetentionSwept));
            Assert.Contains("info RetentionSwept", line);
            Assert.Contains("removed=3", line);
            Assert.Contains("days=30", line);
            Assert.DoesNotContain("error", line);
        }

        [Fact]
        public void Flags_render_as_true_and_false()
        {
            Log.Event(LogEvent.UpdateChecked, ("found", true), ("quiet", false));

            var line = SingleLineOf(nameof(LogEvent.UpdateChecked));
            Assert.Contains("found=true", line);
            Assert.Contains("quiet=false", line);
        }

        [Fact]
        public void An_exception_marks_the_line_as_error()
        {
            Log.Event(LogEvent.TranslationFailed, new InvalidOperationException("boom"));

            var line = SingleLineOf(nameof(LogEvent.TranslationFailed));
            Assert.StartsWith("error TranslationFailed", line.Substring(line.IndexOf(' ') + 1));
            Assert.Contains("InvalidOperationException: boom", line);
        }

        [Fact]
        public void A_throwing_sink_costs_the_caller_nothing()
        {
            Log.Attach(new ThrowingSink());

            Log.Event(LogEvent.AppCrash, new InvalidOperationException("boom"));

            Log.Detach();
        }

        private sealed class ThrowingSink : ILogSink
        {
            public void WriteLine(string line) => throw new IOException("disk on fire");
        }
    }

    [Fact]
    public void A_multiline_exception_message_is_flattened_and_bounded()
    {
        var line = Log.FormatLine(
            new DateTimeOffset(2026, 10, 1, 9, 15, 32, TimeSpan.FromHours(8)),
            LogEvent.TranslationFailed,
            new InvalidOperationException("first\nsecond\r\nthird"),
            []);

        Assert.EndsWith("ex=InvalidOperationException: first second third", line);
    }

    [Fact]
    public void An_exception_long_message_is_truncated()
    {
        var line = Log.FormatLine(
            new DateTimeOffset(2026, 10, 1, 9, 15, 32, TimeSpan.FromHours(8)),
            LogEvent.TranslationFailed,
            new InvalidOperationException(new string('x', 500)),
            []);

        // 300 字符截断加省略号：异常文本可能捎带用户原文，留定位量即可。
        Assert.Matches(@"ex=InvalidOperationException: x{300}…$", line);
    }

    [Fact]
    public void The_first_three_stack_frames_ride_along()
    {
        Exception failure;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception caught)
        {
            failure = caught;
        }

        var line = Log.FormatLine(DateTimeOffset.Now, LogEvent.UnobservedTask, failure, []);

        Assert.Contains(" frames=", line);
        if (failure.StackTrace is { Length: > 0 })
        {
            Assert.Contains("LogTests.", line);
        }
    }

    [Fact]
    public void A_layout_crash_keeps_enough_frames_to_name_the_list()
    {
        // 用户日志 2026-10-19 的形状：第一帧的参数表就有四百多字，原来三帧 600 字的
        // 预算到第三帧就截没了，看不出是哪个列表在量尺寸。
        var stack = string.Join("\r\n",
            "   at System.Windows.Controls.VirtualizingStackPanel.ExtendViewport(IHierarchicalVirtualizationAndScrollInfo virtualizationInfoProvider, Boolean isHorizontal, Rect viewport, VirtualizationCacheLength cacheLength, VirtualizationCacheLengthUnit cacheUnit, Size stackPixelSizeInCacheBeforeViewport, Size stackLogicalSizeInCacheBeforeViewport, Size stackPixelSizeInCacheAfterViewport, Size stackLogicalSizeInCacheAfterViewport, Size stackPixelSize, Size stackLogicalSize, Int32& itemsInExtendedViewportCount)",
            "   at System.Windows.Controls.VirtualizingStackPanel.IsExtendedViewportFull()",
            "   at System.Windows.Controls.VirtualizingStackPanel.MeasureOverrideImpl(Size constraint, Nullable`1& lastPageSafeOffset, List`1& previouslyMeasuredOffsets, Nullable`1& lastPagePixelSize, Boolean remeasure)",
            "   at System.Windows.Controls.VirtualizingStackPanel.MeasureOverride(Size constraint)",
            "   at System.Windows.FrameworkElement.MeasureCore(Size availableSize)",
            "   at System.Windows.Controls.GroupItem.MeasureOverride(Size constraint)",
            "--- End of stack trace from previous location ---",
            @"   at Shiyu.App.LibraryWindow.Reload() in D:\a\shiyu\shiyu\src\Shiyu.App\LibraryWindow.xaml.cs:line 412");

        var frames = Log.CompactFrames(stack);

        Assert.Equal(
            "System.Windows.Controls.VirtualizingStackPanel.ExtendViewport"
            + " | System.Windows.Controls.VirtualizingStackPanel.IsExtendedViewportFull"
            + " | System.Windows.Controls.VirtualizingStackPanel.MeasureOverrideImpl"
            + " | System.Windows.Controls.VirtualizingStackPanel.MeasureOverride"
            + " | System.Windows.FrameworkElement.MeasureCore"
            + " | System.Windows.Controls.GroupItem.MeasureOverride"
            + " | Shiyu.App.LibraryWindow.Reload LibraryWindow.xaml.cs:412",
            frames);
    }

    [Fact]
    public void Compact_frames_stay_bounded()
    {
        var stack = string.Join("\n", Enumerable.Range(0, 40).Select(i =>
            $"   at Shiyu.Core.Deep.Nested.Type{i}.Method{i}(String a, Int32 b)"));

        var frames = Log.CompactFrames(stack);

        Assert.Equal(8, frames.Split(" | ").Length);
        Assert.DoesNotContain("Type8.", frames);
        Assert.True(frames.Length <= 901);
    }

    /// <summary>
    /// 钉住公开签名（O-05 的硬约束）：Log 的公开方法不接受任何 string 参数，
    /// 字段值只可能是数字或布尔。有人哪天手滑加了 string 重载，这条测试先红。
    /// LogField.Key 是例外——度量名由开发者拼写，不是内容。
    /// </summary>
    [Fact]
    public void The_public_API_cannot_accept_string_content()
    {
        foreach (var method in typeof(Log).GetMethods())
        {
            if (method.DeclaringType != typeof(Log))
            {
                continue;
            }

            Assert.True(
                method.GetParameters().All(p => p.ParameterType != typeof(string)),
                $"Log.{method.Name} 长出了 string 参数");
        }

        var valueProperties = typeof(LogField)
            .GetProperties()
            .Where(p => p.Name != nameof(LogField.Key));
        Assert.DoesNotContain(valueProperties, p => p.PropertyType == typeof(string));
    }

    public sealed class FileSink : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "shiyu-log-tests", Guid.NewGuid().ToString("N"));

        // 固定本地时钟：文件名与保留窗口都断言得出，也不受测机时区摆布。
        private readonly DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly TimeProvider _clock;

        public FileSink()
        {
            // logs 子目录本由 sink 首写时创建；测试要预先放好旧文件/占位
            // 文件，所以这里先建。
            Directory.CreateDirectory(Path.Combine(_directory, "logs"));
            _clock = new LocalClock(_now);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
        }

        private FileLogSink Sink() => new(_directory, _clock);

        private string PathFor(DateTime date)
            => Path.Combine(_directory, "logs", $"shiyu-{date:yyyyMMdd}.log");

        [Fact]
        public void Lines_land_in_a_dated_file_under_logs()
        {
            Sink().WriteLine("one");

            Assert.Equal(
                Path.Combine(_directory, "logs", "shiyu-20261001.log"),
                PathFor(new DateTime(2026, 10, 1)));
            Assert.Equal("one" + Environment.NewLine, File.ReadAllText(PathFor(new DateTime(2026, 10, 1))));
        }

        [Fact]
        public void Files_older_than_seven_days_are_swept_once_a_day()
        {
            File.WriteAllText(PathFor(new DateTime(2026, 9, 1)), "old");
            File.WriteAllText(PathFor(new DateTime(2026, 9, 1)) + ".1", "old rotation");
            File.WriteAllText(PathFor(new DateTime(2026, 9, 24)), "edge: exactly 7 days, kept");

            Sink().WriteLine("fresh");

            Assert.False(File.Exists(PathFor(new DateTime(2026, 9, 1))));
            Assert.False(File.Exists(PathFor(new DateTime(2026, 9, 1)) + ".1"));
            Assert.True(File.Exists(PathFor(new DateTime(2026, 9, 24))));
        }

        [Fact]
        public void A_file_past_the_cap_rotates_instead_of_growing()
        {
            var today = PathFor(new DateTime(2026, 10, 1));
            File.WriteAllText(today, new string('a', 1024 * 1024));

            var sink = Sink();
            sink.WriteLine("the overflow line");

            Assert.True(File.Exists(today + ".1"));
            Assert.StartsWith(new string('a', 100), File.ReadAllText(today + ".1"));
            Assert.Equal("the overflow line" + Environment.NewLine, File.ReadAllText(today));
        }

        [Fact]
        public void A_directory_blocking_the_log_file_is_swallowed()
        {
            // 与日志文件同名的目录让 AppendAllText 必败：sink 不得把 IO 异常
            // 递给调用方——诊断系统自己不能成为崩溃源。
            Directory.CreateDirectory(PathFor(new DateTime(2026, 10, 1)));

            Sink().WriteLine("into the void");

            Assert.True(Directory.Exists(PathFor(new DateTime(2026, 10, 1))));
        }

        private sealed class LocalClock(DateTimeOffset now) : TimeProvider
        {
            // GetLocalNow() 不是虚方法（= GetUtcNow + LocalTimeZone）：
            // 两个都钉住，本地读数才正好是构造值。
            public override DateTimeOffset GetUtcNow() => now;

            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        }
    }
}
