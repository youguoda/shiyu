namespace Shiyu.Core;

/// <summary>One taught gesture: the key (or mouse move) on a cap, the action beside it.</summary>
public sealed record TeachStep(string Key, string Action);

/// <summary>
/// 预览尾行的操作教学（设置项「悬停教学提示」，bar.card-tooltips）。
///
/// 用户需求 2026-10-05：这一行要更像这个应用。原来是一整句灰字——"双击粘贴到
/// 原来的窗口；Enter 粘贴选中项；按住左键拖出：文本入编辑器（带格式）、图片入
/// 聊天窗、文件入资源管理器"——折成两行、在括号中间断开。现在是三枚键帽加短
/// 动作，一行放得下；拖放只说这张卡自己的去处，不再替三种类型各说一遍。
///
/// 回车的键帽取自键位表（键位即数据，§5.2/O-42）：改表即改帽。
/// </summary>
public static class PreviewTeaching
{
    public static IReadOnlyList<TeachStep> For(EntryKind kind) =>
    [
        new("双击", "粘贴到原窗口"),
        new(KeyMap.BadgeText("paste") ?? "Enter", "粘贴选中项"),
        new("拖出", kind switch
        {
            EntryKind.Image => "到聊天窗",
            EntryKind.Files => "到资源管理器",
            _ => "到编辑器（带格式）",
        }),
    ];
}
