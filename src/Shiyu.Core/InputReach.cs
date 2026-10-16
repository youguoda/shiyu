namespace Shiyu.Core;

/// <summary>
/// 拾语发出的模拟按键能不能到达目标窗口（v3 票 30 / 评审 2026-10-16 F-03）。Windows 的用户界面特权隔离
/// （UIPI）会静默丢弃低完整性级别进程发往高完整性级别窗口的模拟输入：拾语以普通权限运行，对以管理员
/// 身份运行的窗口发 Ctrl+V，SendInput 不报错，按键却被吞掉——反向输入框约 0.4 秒后还把剪贴板还原了，
/// 译文既没贴上、也不在剪贴板里。
///
/// 完整性级别按 RID 比较：低 0x1000、中 0x2000、高 0x3000（管理员）、系统 0x4000。
/// </summary>
public static class InputReach
{
    public const uint Medium = 0x2000;

    /// <summary>
    /// 模拟输入会不会被挡下。目标的级别读不到（null）按"高于"算：读不到通常恰恰因为对方是提权
    /// 进程、令牌不让看——宁可多提示一句，也不让译文无声地消失。
    /// </summary>
    public static bool Blocked(uint? target, uint self) => target is not { } level || level > self;
}
