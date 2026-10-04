namespace Shiyu.App;

/// <summary>
/// 调试覆盖的唯一入口（O-41）：一切 <c>SHIYU_*</c> 环境变量的读取都收在这
/// 一个类里。Debug 构建读环境；Release 构建把它们编译成常量——发布产物里
/// 连变量名的字面量都不复存在（验证：对 Release 的 exe 做二进制扫描，
/// <c>SHIYU_</c> 的 ASCII 与 UTF-16 编码均无命中），生产行为一字不变。
///
/// 此前 <c>SHIYU_RELAY_URL</c>、<c>SHIYU_DATA_DIR</c> 的第二处读取、
/// <c>SHIYU_OPEN_SETTINGS</c>、<c>SHIYU_OPEN_UPDATE</c> 在 Release 里也被
/// 认——一台设置了它们的机器上，发布版会悄悄改道。探针（票 15）与实机
/// 验收（票 06）依赖这些旋钮，生产从不。
/// </summary>
internal static class DebugOverrides
{
#if DEBUG
    /// <summary>探针模式（票 15）：SHIYU_DATA_DIR 指向隔离目录才成立。</summary>
    public static bool IsProbe => ProbeDirectory is not null;

    /// <summary>
    /// 探针的数据目录；null 表示普通启动。设置搬进探针目录也一样由它
    /// 驱动——读真设置会把探针指到用户的后端上。
    /// </summary>
    public static string? ProbeDirectory =>
        Environment.GetEnvironmentVariable("SHIYU_DATA_DIR") is { Length: > 0 } directory
            ? directory
            : null;

    /// <summary>
    /// 中转端点覆盖：骑在设置 store 的不落盘旁路上——读得到，文件从不
    /// 认识它。
    /// </summary>
    public static string? RelayUrl =>
        Environment.GetEnvironmentVariable("SHIYU_RELAY_URL") is { Length: > 0 } url
            ? url
            : null;

    /// <summary>启动即打开设置窗——自动化检查不必找托盘图标。</summary>
    public static bool OpenSettings =>
        Environment.GetEnvironmentVariable("SHIYU_OPEN_SETTINGS") == "1";

    /// <summary>启动即打开更新窗——手工流程可以端到端驱动。</summary>
    public static bool OpenUpdate =>
        Environment.GetEnvironmentVariable("SHIYU_OPEN_UPDATE") == "1";

    /// <summary>实机验收 UI 线程兜底的隐藏命令（票 06）：3 秒后抛一次。</summary>
    public static bool DebugTickCrash =>
        Environment.GetEnvironmentVariable("SHIYU_DEBUG_TICK_CRASH") == "1";

    /// <summary>探针的呼出通道（票 15）：bar|panel|library|settings|quickbar|reverse。</summary>
    public static string? ProbeCommand =>
        Environment.GetEnvironmentVariable("SHIYU_PROBE_CMD");

    /// <summary>
    /// 反向输入框用确定性的假后端（票 43）：返回 "[EN] " + 原文，不碰网络。端到端探针靠它得到
    /// 可断言的输出。
    /// </summary>
    public static bool FakeBackend =>
        Environment.GetEnvironmentVariable("SHIYU_FAKE_BACKEND") == "1";

    /// <summary>
    /// 探针模式下照常监听剪贴板（票 43）。探针默认不监听——用户的复制不能进探针的数据库——但
    /// "回贴的写入与还原不产生历史条目"要在端到端里核对，就得让监听在场；这只在探针自己的目录里
    /// 记录探针自己放上去的合成文字。
    /// </summary>
    public static bool ProbeClipboard =>
        Environment.GetEnvironmentVariable("SHIYU_PROBE_CLIPBOARD") == "1";

    /// <summary>让设置窗直接落到某个设置项（既有深链）。</summary>
    public static string? ProbeItem =>
        Environment.GetEnvironmentVariable("SHIYU_PROBE_ITEM") is { Length: > 0 } item
            ? item
            : null;

    /// <summary>给面板一句要翻译的话。</summary>
    public static string? ProbeText =>
        Environment.GetEnvironmentVariable("SHIYU_PROBE_TEXT");

    /// <summary>
    /// 探针互斥量名：数据目录路径的稳定哈希。同一目录的探针仍互斥，
    /// 不同目录、以及与用户的 <c>Shiyu</c>，永不相撞。路径大写归一，
    /// 因为 Windows 把互斥量名当不区分大小写处理而目录写法人人不同。
    /// </summary>
    public static string ProbeMutexName(string dataDirectory)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(dataDirectory.Trim().ToUpperInvariant()));
        return "Shiyu-probe-" + Convert.ToHexString(hash)[..16];
    }
#else
    // 发布构建：常量返回，无环境读取（O-41）。JIT 直接折叠，调用点的分支
    // 整体消失——发布版在这些旋钮上的行为与一台什么都没设置的机器相同。
    public static bool IsProbe => false;
    public static string? ProbeDirectory => null;
    public static string? RelayUrl => null;
    public static bool OpenSettings => false;
    public static bool OpenUpdate => false;
    public static bool DebugTickCrash => false;
    public static string? ProbeCommand => null;
    public static bool FakeBackend => false;
    public static bool ProbeClipboard => false;
    public static string? ProbeItem => null;
    public static string? ProbeText => null;
#endif
}
