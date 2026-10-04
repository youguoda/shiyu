using System.Text.RegularExpressions;

namespace Shiyu.Core.Tests;

/// <summary>
/// 票 18（评审 R4/R6）的静态看门人：文字的层级只允许来自 DesignTokens。
///
/// 两个已知会绕过 <see cref="DesignTokenTests"/> 的后门，在 Themes/ 之外一律
/// 不许出现：
/// 作用于文字的 <c>Opacity &lt; 1</c> 会把令牌色压暗而不经过 ReadablePairs
/// 的对比度计算（评审实测说明文字只有 3.5:1）；数字字面量 FontSize 会绕过
/// 字号阶梯（评审实测设置窗值回落到 12、比标签还小）。
///
/// 白名单机制：确属「装饰而非文字」的例外登记在
/// <see cref="AllowedOpacityLiterals"/>，按（相对路径 + 行内标记）匹配——
/// 只有当违规行包含该标记时才算放行。新增条目必须同时在条目旁写明理由；
/// 名单之外的透明度请改用颜色令牌（如 Brush.TextSecondary）表达层级。
/// Themes/Controls.xaml 本身是令牌的消费者（滚动条拇指等装饰），整目录豁免。
/// </summary>
public class TextTokenHygieneTests
{
    // (仓库相对路径, 行内须包含的标记)。命中标记的行视为已核实的装饰例外。
    private static readonly (string Path, string Marker)[] AllowedOpacityLiterals =
    [
        // 深链定位的三段衰减水洗层：Border 覆盖层，不是文字。
        ("src/Shiyu.App/SettingsWindow.xaml.cs", "wash.Opacity"),
        // 预览窗自绘阴影的强度（DropShadowEffect 的 Opacity）。
        ("src/Shiyu.App/PreviewWindow.xaml", "DropShadowEffect"),
    ];

    // 字号没有例外：任何数字都应换成 Size.* 资源引用。
    private static readonly (string Path, string Marker)[] NoFontSizeExceptions = [];

    /// <summary>Opacity 赋一个小数字面量（0.x），XAML 与 C# 两种写法都抓。</summary>
    private static readonly Regex TextOpacityLiteral =
        new(@"Opacity\s*=\s*""?0?\.\d", RegexOptions.Compiled);

    /// <summary>FontSize 赋一个数字字面量；Size.* 资源与 DesignTokens 常量不以数字开头，不会误伤。</summary>
    private static readonly Regex NumericFontSizeLiteral =
        new(@"FontSize\s*=\s*""?[0-9]", RegexOptions.Compiled);

    [Fact]
    public void No_text_opacity_literals_outside_the_theme_dictionary()
    {
        var offenders = Scan(TextOpacityLiteral, AllowedOpacityLiterals);

        Assert.True(offenders.Count == 0,
            "文字上的透明度字面量绕过了对比度测试，请改用颜色令牌（白名单例外见 "
            + nameof(TextTokenHygieneTests) + "）：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void No_numeric_font_size_literals_outside_the_theme_dictionary()
    {
        var offenders = Scan(NumericFontSizeLiteral, NoFontSizeExceptions);

        Assert.True(offenders.Count == 0,
            "数字字面量字号绕过了字号阶梯，请改用 Size.* 资源或 DesignTokens 常量：\n"
            + string.Join("\n", offenders));
    }

    private static List<string> Scan(Regex pattern, (string Path, string Marker)[] allowed)
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var markersHere = allowed
                .Where(entry => entry.Path == relative)
                .Select(entry => entry.Marker)
                .ToList();

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!pattern.IsMatch(lines[index]))
                {
                    continue;
                }

                if (markersHere.Any(marker => lines[index].Contains(marker)))
                {
                    continue;
                }

                offenders.Add($"{relative}:{index + 1}: {lines[index].Trim()}");
            }
        }

        return offenders;
    }

    private static IEnumerable<string> SourceFiles(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "src", "Shiyu.App"),
                "*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsUnder(path, "Themes") && !IsUnder(path, "obj") && !IsUnder(path, "bin"));

    private static bool IsUnder(string path, string folder)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(folder, StringComparer.OrdinalIgnoreCase);

    /// <summary>从测试程序集的位置向上找仓库根（含 src/Shiyu.App 的目录）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "src", "Shiyu.App")))
        {
            dir = dir.Parent!;
        }

        return dir is not null
            ? dir.FullName
            : throw new InvalidOperationException("找不到仓库根：测试必须从仓库内运行。");
    }
}
