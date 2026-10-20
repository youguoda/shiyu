namespace Shiyu.Core;

/// <summary>
/// 钉住的预览工作台按形态出的动作芯片表（评审方案 2026-10-20）。纯表：
/// 条目形态/子类型 → (显示名, 动作名) 序列；动作名的执行者在宿主（窄条），
/// 预览自己不持有剪贴板与库。
/// </summary>
public static class WorkbenchChips
{
    /// <summary>一枚芯片：显示名 + 动作名（动作名走宿主的执行表）。</summary>
    public sealed record Chip(string Label, string Action);

    public static IReadOnlyList<Chip> For(EntryKind kind, EntrySubtype subtype) => kind switch
    {
        EntryKind.Files =>
        [
            new Chip("打开", "open-path"),
            new Chip("定位", "locate-path"),
            new Chip("复制路径", "copy"),
        ],
        EntryKind.Image =>
        [
            new Chip("复制图片", "copy-image"),
        ],
        _ => subtype switch
        {
            EntrySubtype.Link =>
            [
                new Chip("打开", "open-uri"),
                new Chip("复制", "copy"),
                new Chip("存为新条目", "save-as-new"),
            ],
            EntrySubtype.Email =>
            [
                new Chip("写邮件", "open-uri"),
                new Chip("复制", "copy"),
                new Chip("存为新条目", "save-as-new"),
            ],
            EntrySubtype.LocalPath =>
            [
                new Chip("打开", "open-path"),
                new Chip("定位", "locate-path"),
                new Chip("复制", "copy"),
            ],
            EntrySubtype.Color =>
            [
                new Chip("复制色值", "copy"),
            ],
            _ =>
            [
                new Chip("复制", "copy"),
                new Chip("存为新条目", "save-as-new"),
            ],
        },
    };
}
