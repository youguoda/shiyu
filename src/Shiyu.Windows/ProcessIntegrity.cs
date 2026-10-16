using System.Runtime.InteropServices;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// 进程的完整性级别（v3 票 30 / 评审 2026-10-16 F-03）：判定交给 Core 的 <see cref="InputReach"/>，这里只取值。
/// 窗口 → 进程号 → 以"有限查询"打开进程 → 令牌 → TokenIntegrityLevel 里那个 SID 的最后一段（RID）。
/// 任何一步不让做（多半恰恰因为对方是提权进程）都给 null。
/// </summary>
public static class ProcessIntegrity
{
    private static readonly Lazy<uint> SelfLevel = new(() => OfProcess(NativeMethods.GetCurrentProcess()) ?? InputReach.Medium);

    /// <summary>拾语自己的级别；读不到按普通权限（中）算。</summary>
    public static uint Self => SelfLevel.Value;

    /// <summary>拾语发给这扇窗的模拟按键会不会被 UIPI 吞掉。</summary>
    public static bool BlocksInput(IntPtr window) => InputReach.Blocked(OfWindow(window), Self);

    public static uint? OfWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0)
        {
            return null;
        }

        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return OfProcess(process);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static uint? OfProcess(IntPtr process)
    {
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            // 先问要多大，再取：TOKEN_MANDATORY_LABEL 开头是 SID 指针，SID 本身跟在同一块内存里。
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, needed, out _))
                {
                    return null;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(sid));
                return count == 0
                    ? null
                    : (uint)Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
