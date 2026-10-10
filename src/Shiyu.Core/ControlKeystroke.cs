namespace Shiyu.Core;

/// <summary>
/// 模拟一次 Ctrl+键（取词的 Ctrl+C、回贴的 Ctrl+V）该发哪些按键——只看此刻哪些修饰键被按着。
/// 平台层照着发；这里是可测的那一半。
/// </summary>
public static class ControlKeystroke
{
    public const ushort Control = 0x11;
    public const ushort Shift = 0x10;
    public const ushort Alt = 0x12;
    public const ushort LeftWin = 0x5B;
    public const ushort RightWin = 0x5C;

    /// <summary>一次按下或抬起。</summary>
    public readonly record struct Stroke(ushort Key, bool Up);

    /// <summary>
    /// Shift、Alt、Win 还按着就先替用户抬起——不然 Ctrl+C 到了应用那里是 Ctrl+Shift+C。Ctrl 已经按着
    /// 就借这一下：不另按，也<b>不替用户松开</b>——替他松开，他还按着的 Ctrl 在 Windows 眼里就没了，
    /// 接下来的 Ctrl 快捷键都不灵，直到他重按一次（用户实录 2026-10-10）。没按着就自己按下、松开。
    /// 调用方正常会先等手指离开修饰键（<see cref="SelectionCapture"/>），这里多半走"什么都没按着"。
    /// </summary>
    public static IReadOnlyList<Stroke> For(ushort key, Func<ushort, bool> isDown)
    {
        var strokes = new List<Stroke>();
        foreach (var modifier in new[] { Shift, Alt, LeftWin, RightWin })
        {
            if (isDown(modifier))
            {
                strokes.Add(new Stroke(modifier, Up: true));
            }
        }

        var borrowed = isDown(Control);
        if (!borrowed)
        {
            strokes.Add(new Stroke(Control, Up: false));
        }

        strokes.Add(new Stroke(key, Up: false));
        strokes.Add(new Stroke(key, Up: true));

        if (!borrowed)
        {
            strokes.Add(new Stroke(Control, Up: true));
        }

        return strokes;
    }

    /// <summary>修饰键里还有没有一个按着——等手指离开时问的就是它。</summary>
    public static bool AnyModifierDown(Func<ushort, bool> isDown)
        => isDown(Control) || isDown(Shift) || isDown(Alt) || isDown(LeftWin) || isDown(RightWin);
}
