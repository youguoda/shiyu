using Shiyu.Core;

namespace Shiyu.Core.Tests.Fakes;

/// <summary>Stands in for the file sink so the facade's contract can be asserted
/// without touching a disk.
///
/// 加锁、读时给快照：门面是进程级的，并行跑的别的测试类驱动的产品路径会同时往里写，
/// 裸 List 边写边枚举会偶发"集合已修改"，LogTests 因此掷过骰子（2026-10-16 一次红）。</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public void WriteLine(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }
}
