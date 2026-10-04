namespace Shiyu.Core.Tests;

/// <summary>
/// The exe icon carries every size Windows asks for: the tray at 16/20/24/32 px
/// (100–200% scaling), the taskbar up to 48, Explorer up to 256. A missing frame
/// means the shell scales a neighbour, and a 32 px glyph shrunk to 16 px is the
/// smudge the old icon showed. Regenerate with tools/icon/render-icon.ps1.
/// </summary>
public class AppIconTests
{
    [Fact]
    public void The_app_icon_has_every_size_windows_asks_for()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "Shiyu.App", "Assets", "app.ico"));

        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2)); // ICONDIR type: 1 = icon
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = Enumerable.Range(0, count)
            .Select(i => bytes[6 + 16 * i] is 0 ? 256 : bytes[6 + 16 * i]) // 0 encodes 256
            .ToHashSet();

        foreach (var expected in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
        {
            Assert.Contains(expected, sizes);
        }
    }

    /// <summary>从测试程序集的位置向上找仓库根（含 src/Shiyu.Core 的目录）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Shiyu.Core")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根：测试必须从仓库内运行。");
    }
}
