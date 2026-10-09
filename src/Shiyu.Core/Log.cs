namespace Shiyu.Core;

/// <summary>
/// 诊断事件名（O-05）。枚举而非自由字符串：事件名不由调用方拼写，
/// 日志里出现什么词由本文件一次定义。往这里加事件时挑一个贴切的
/// 既有名字优先——事件越少，翻日志的人越读得过来。
/// </summary>
public enum LogEvent
{
    // 剪贴板记录
    ClipboardRecorded,
    ClipboardImageFailed,
    ClipboardRestoreFailed,

    // 翻译与词典
    TranslationFailed,
    BatchTranslationFailed,
    AgentActionFailed,
    DictionaryLookupFailed,

    /// <summary>翻译记录写不进库（用户需求 2026-10-09）。翻译本身已成，只留痕迹不打扰。</summary>
    TranslationLogFailed,

    // 更新
    UpdateChecked,
    UpdateCheckFailed,
    UpdateApplyFailed,

    // 保留清理
    RetentionSwept,
    RetentionSweepFailed,

    // 设置与备份
    SettingsSaved,
    SettingsSaveFailed,
    BackupOperationFailed,

    // 进程级（三个全局处理器的落点）
    StartupFailed,
    AppCrash,
    UnobservedTask,
    FatalExit,

    // 界面动作
    OpenLinkFailed,
}

/// <summary>
/// 一条附加字段：键是开发者拼的度量名，值只能是数字或布尔。这个类型
/// 存在的意义是把"日志里绝无正文"做成编译期事实——剪贴板文本、译文、
/// 密钥想进日志，得先挤进一个 long。
/// </summary>
public readonly record struct LogField(string Key, long Number, bool Flag)
{
    public static implicit operator LogField((string key, int value) field)
        => new(field.key, field.value, Flag: false);

    public static implicit operator LogField((string key, long value) field)
        => new(field.key, field.value, Flag: false);

    public static implicit operator LogField((string key, bool value) field)
        => new(field.key, field.value ? 1 : 0, Flag: true);
}

/// <summary>日志的出口端口。默认无人挂载（丢弃）；App 组合根挂文件 sink。</summary>
public interface ILogSink
{
    /// <summary>收走一行。必须同步完成、不得抛——致命路径靠它落盘。</summary>
    void WriteLine(string line);
}

/// <summary>
/// 日志门面（O-05）：API 从类型上杜绝把内容写进日志——只接受事件枚举、
/// 异常对象、数值/布尔字段，没有任何字符串正文参数。写入失败静默放弃：
/// 诊断系统自己不能成为崩溃源。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static ILogSink? _sink;

    /// <summary>组合根在数据目录定下来之后调用一次。</summary>
    public static void Attach(ILogSink sink)
    {
        lock (Gate)
        {
            _sink = sink;
        }
    }

    /// <summary>测试的隔离手段；产品代码不调用。</summary>
    public static void Detach()
    {
        lock (Gate)
        {
            _sink = null;
        }
    }

    /// <param name="fields">数值/布尔字段；元组字面量可直接传入。</param>
    public static void Event(LogEvent @event, params LogField[] fields)
        => Write(@event, null, fields);

    public static void Event(LogEvent @event, Exception exception, params LogField[] fields)
        => Write(@event, exception, fields);

    private static void Write(LogEvent @event, Exception? exception, LogField[] fields)
    {
        try
        {
            ILogSink? sink;
            lock (Gate)
            {
                sink = _sink;
            }

            sink?.WriteLine(FormatLine(DateTimeOffset.Now, @event, exception, fields));
        }
        catch
        {
            // sink 抛了就放弃这一行：日志不值得为它自己崩掉进程。
        }
    }

    /// <summary>一行的形状：<c>时间 级别 事件 字段…</c>。纯函数，便于钉住格式。</summary>
    internal static string FormatLine(
        DateTimeOffset timestamp, LogEvent @event, Exception? exception, LogField[] fields)
    {
        // 级别不单独设 API：带异常即错误，其余是值得留痕的状态变化。
        var level = exception is null ? "info" : "error";
        var line = $"{timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {level} {@event}";

        foreach (var field in fields)
        {
            line += " " + field.Key + "=" + (field.Flag ? (field.Number != 0 ? "true" : "false") : field.Number.ToString());
        }

        if (exception is not null)
        {
            // 消息压成一行并截断：异常文本可能带着用户的原文（比如翻译失败
            // 的原因），留前几百字足够定位，又不至于把正文整个誊进日志。
            line += " ex=" + exception.GetType().Name
                + ": " + Flatten(exception.Message, 300);

            var frames = exception.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (frames is { Length: > 0 })
            {
                // 前三帧足够指认案发现场；再往后基本是消息泵和运行时。
                line += " frames=" + Flatten(string.Join(" | ", frames.Take(3)), 600);
            }
        }

        return line;
    }

    private static string Flatten(string text, int limit)
    {
        // \r\n 先合并成一个空格，否则 Windows 换行会把两处替换叠成双空格。
        var flat = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= limit ? flat : flat[..limit] + "…";
    }
}
