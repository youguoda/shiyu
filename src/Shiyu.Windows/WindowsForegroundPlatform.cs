using System.Runtime.InteropServices;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// <see cref="IForegroundPlatform"/> 的 Win32 实现（票 43）：与 Core 里的前台兜底序列
/// （<see cref="ForegroundReclaim"/>）一一对应的薄壳，序列本身的时序与取舍在 Core 里有测试。
/// </summary>
internal sealed class WindowsForegroundPlatform : IForegroundPlatform
{
    public static readonly WindowsForegroundPlatform Instance = new();

    public IntPtr Foreground() => NativeMethods.GetForegroundWindow();

    public bool SetForeground(IntPtr window) => NativeMethods.SetForegroundWindow(window);

    public void AllowAnyProcess() => NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);

    public uint ThreadOf(IntPtr window)
        => window == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(window, out _);

    public uint CurrentThread() => NativeMethods.GetCurrentThreadId();

    public bool Attach(uint thread, uint toThread, bool attach)
        => NativeMethods.AttachThreadInput(thread, toThread, attach);

    /// <summary>
    /// 发一次 F24（按下、抬起）。这是这个类里<b>唯一</b>发键的地方，也只发 F24——
    /// 绝不发 Alt：单按 Alt 会激活记事本、Office 的菜单栏，吞掉随后的 Ctrl+V。
    /// </summary>
    public void TapF24()
    {
        var inputs = new[] { Key(up: false), Key(up: true) };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());

        static NativeMethods.Input Key(bool up) => new()
        {
            type = NativeMethods.InputKeyboard,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KeyboardInput
                {
                    wVk = NativeMethods.VkF24,
                    dwFlags = up ? NativeMethods.KeyEventKeyUp : 0,
                },
            },
        };
    }

    public void Pause(TimeSpan duration) => Thread.Sleep(duration);
}
