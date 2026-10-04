namespace Shiyu.Core;

public enum SettingsControl
{
    /// <summary>Exclusive choice shown as one row of joined buttons.</summary>
    Segmented,

    Toggle,
    Number,
    Text,

    /// <summary>A secret: never echoed back, blank means keep the stored one.</summary>
    Password,

    /// <summary>A hotkey combination, written like Ctrl+Shift+Z.</summary>
    Hotkey,

    /// <summary>An ordered list of named actions, comma separated.</summary>
    Actions,

    /// <summary>Free-form text across lines.</summary>
    Multiline,

    /// <summary>A folder path, with a browse affordance.</summary>
    Directory,

    /// <summary>Shown, not edited.</summary>
    ReadOnly,

    /// <summary>A row the window builds itself (e.g. the backup buttons).</summary>
    Custom,

    /// <summary>A dropdown: exactly one of <see cref="SettingsItem.ChoiceList"/>, picked not typed.</summary>
    Choice,

    /// <summary>A read-only reference card that jumps to the item's real home (§5.1 引用卡).</summary>
    Link,
}

/// <summary>
/// One setting as data: what it is, how it is edited, where its words live.
/// The interface renders the tree and nothing else — adding a setting adds a
/// leaf here plus a value binding, and the renderer never learns about it.
/// </summary>
/// <param name="Unit">数值项的单位后缀（天、毫秒、行、px、条），数值必带单位（U-14）。</param>
/// <param name="Icon">Fluent 字形码位（如 "E713"），设置卡左缘 20 DIP 图标。</param>
/// <param name="FullBleed">编辑器占满卡的整行（标签与说明在上方）——给天生高瘦的编辑器（三层排除、动作清单）用。</param>
public sealed record SettingsItem(
    string Id,
    string Label,
    SettingsControl Control,
    string? Hint = null,
    double Min = 0,
    double Max = 0,
    string[]? Choices = null,
    string? Parent = null,
    string[]? Keywords = null,
    string? Unit = null,
    string? Icon = null,
    bool FullBleed = false)
{
    public string[] ChoiceList => Choices ?? [];
    public string[] KeywordList => Keywords ?? [];
}

public sealed record SettingsSection(string Id, string Title, params SettingsItem[] Items);

/// <param name="Icon">导航字形码位；关于页固定在导航底部（§5.1）。</param>
/// <param name="Description">页标题下的一句话（§6.3 内容层级第二行），可空。</param>
public sealed record SettingsPage(
    string Id,
    string Title,
    string Icon = "",
    string? Description = null,
    params SettingsSection[] Sections);

public static class SettingsSchema
{
    /// <summary>
    /// The whole settings surface, organised by what the user came to do —
    /// the five pages of §5.1 plus About pinned at the bottom of the
    /// navigation. Page names are directions ("翻译"), never module names
    /// ("服务"); an action list belongs to the bar, not to a page of its own.
    ///
    /// Every id that existed before the five-page redesign still exists with
    /// the same meaning — pages moved around their items, items never
    /// renamed underneath the settings file, the search keywords, or the
    /// deep links.
    /// </summary>
    public static readonly IReadOnlyList<SettingsPage> Tree =
    [
        new SettingsPage("general", "常规", "E713", "启动、外观，以及数据放在哪。",
            new SettingsSection("general.startup", "启动",
                new SettingsItem(
                    "store.start-with-windows", "开机自启", SettingsControl.Toggle,
                    Hint: "登录 Windows 后自动在后台运行。",
                    Keywords: ["开机", "自启", "启动", "开机自动"],
                    Icon: "E7E8")),
            new SettingsSection("general.appearance", "外观",
                new SettingsItem(
                    "theme", "主题", SettingsControl.Segmented,
                    Choices: ["跟随系统", "浅色", "深色"],
                    Hint: "界面配色跟随 Windows，或固定一档。",
                    Keywords: ["主题", "深色", "浅色", "夜间", "夜间模式", "变暗"],
                    Icon: "E790")),
            new SettingsSection("general.performance", "性能",
                new SettingsItem(
                    "look.lightweight", "轻量模式", SettingsControl.Toggle,
                    Hint: "窄条隐藏后释放界面资源、压缩常驻内存；期间复制的内容照常记录。",
                    Keywords: ["内存", "占用", "轻量", "后台", "常驻"],
                    Icon: "E713")),
            new SettingsSection("general.data", "数据",
                new SettingsItem(
                    "store.directory", "数据位置", SettingsControl.Directory,
                    Hint: "历史与图片存在这里；留空用默认位置。移动后重启拾语生效。",
                    Keywords: ["位置", "目录", "数据", "移动", "迁移", "换盘"],
                    Icon: "E8B7"),
                new SettingsItem(
                    "store.usage", "磁盘占用", SettingsControl.Custom,
                    Hint: "数据库、图片原图与合计占用；可一键打开数据所在文件夹。",
                    Keywords: ["占用", "磁盘", "大小", "空间", "多少"],
                    Icon: "E7C3"),
                new SettingsItem(
                    "store.backup", "备份", SettingsControl.Custom,
                    Hint: "导出全部历史、图片原图与设置；可加密。导入可合并或覆盖。",
                    Keywords: ["备份", "导出", "导入", "加密"],
                    Icon: "E74E"))),

        new SettingsPage("bar", "窄条", "E77F", "光标旁的常驻工具条：位置、密度与悬停动作。",
            new SettingsSection("bar.behaviour", "呼出与置顶",
                new SettingsItem(
                    "bar.at-cursor", "光标旁呼出", SettingsControl.Toggle,
                    Hint: "窄条出现在光标/输入位置附近，与系统 Win+V 面板一致；关闭则固定在你上次拖放的位置。",
                    Keywords: ["位置", "光标", "呼出", "弹出", "输入"],
                    Icon: "E7B3"),
                new SettingsItem(
                    "look.bar-topmost", "保持在最前", SettingsControl.Toggle,
                    Hint: "窄条保持在其他窗口之上；关闭后可被其他窗口遮挡，置顶状态由窄条头部的图钉按钮随时切换。",
                    Keywords: ["置顶", "最前", "保持在最前", "图钉", "压住", "遮挡", "窗口", "浮在最上层"],
                    Icon: "E718")),
            new SettingsSection("bar.density", "密度",
                new SettingsItem(
                    "bar.text-lines", "文本行数", SettingsControl.Number, Min: 1, Max: 20,
                    Hint: "窄条里每张文本卡最多显示的行数。",
                    Keywords: ["行数", "密度", "文本"],
                    Unit: "行",
                    Icon: "E8D2"),
                new SettingsItem(
                    "bar.image-height", "图片高度", SettingsControl.Number, Min: 40, Max: 400,
                    Hint: "窄条里图片卡的显示高度。",
                    Keywords: ["图片", "高度", "密度"],
                    Unit: "px",
                    Icon: "E8B9"),
                new SettingsItem(
                    "bar.file-count", "文件条数", SettingsControl.Number, Min: 1, Max: 10,
                    Hint: "文件卡里最多列出的文件行数。",
                    Keywords: ["文件", "条数", "密度"],
                    Unit: "条",
                    Icon: "E8B7"),
                new SettingsItem(
                    "look.preview-hover", "悬停预览延迟", SettingsControl.Number, Min: 0, Max: 2000,
                    Hint: "鼠标在卡片上停留多少毫秒后弹出完整预览；0 表示关闭悬停预览（按住空格仍可预览）。",
                    Keywords: ["预览", "悬停", "停留", "空格", "完整", "延迟"],
                    Unit: "毫秒",
                    Icon: "E823"),
                new SettingsItem(
                    "bar.card-tooltips", "悬停教学提示", SettingsControl.Toggle,
                    Hint: "悬停预览面板底部的一行拖放教学（双击粘贴、拖出等）；关闭后预览不再显示教学行。",
                    Keywords: ["提示", "悬停", "教学", "拖放", "拖出", "预览"],
                    Icon: "E823")),
            new SettingsSection("bar.actions", "悬停动作",
                new SettingsItem(
                    "bar.actions", "动作清单", SettingsControl.Actions,
                    Hint: "悬停卡片时托盘里出现的动作与顺序；关闭的动作不再出现。上下拖动调整顺序。",
                    Keywords: ["悬停", "托盘", "按钮", "复制", "粘贴", "删除", "动作", "顺序"],
                    Icon: "E8C8",
                    FullBleed: true),
                new SettingsItem(
                    "action.sound", "完成提示音", SettingsControl.Toggle,
                    Hint: "动作完成后播放一声提示；默认只显示对勾。",
                    Keywords: ["声音", "提示音", "反馈", "音效"],
                    Icon: "E767"))),

        new SettingsPage("privacy", "记录与隐私", "E72E", "什么被记录、什么绝不记录、留多久。",
            new SettingsSection("privacy.record", "记录什么",
                new SettingsItem(
                    "record.text", "记录文本", SettingsControl.ReadOnly,
                    Hint: "始终记录——没有它拾语就不成立。",
                    Keywords: ["文本", "记录"],
                    Icon: "E8D2"),
                new SettingsItem(
                    "record.images", "记录图片", SettingsControl.Toggle,
                    Hint: "复制图片内容时是否入库。",
                    Keywords: ["图片", "截图", "记录"],
                    Icon: "E8B9"),
                new SettingsItem(
                    "record.files", "记录文件", SettingsControl.Toggle,
                    Hint: "复制文件时是否入库（文件列表与图片文件预览）。",
                    Keywords: ["文件", "记录"],
                    Icon: "E8B7")),
            new SettingsSection("privacy.exclusions", "排除",
                new SettingsItem(
                    "exclusions", "排除规则", SettingsControl.Custom,
                    Hint: "内置清单始终生效；你自己的排除可从正在运行的应用添加；高级规则用 re: 正则匹配内容。",
                    Keywords: ["排除", "隐私", "密码", "不记录", "敏感", "正则", "密码管理器"],
                    Icon: "E72E",
                    FullBleed: true)),
            new SettingsSection("privacy.retention", "保留与保护",
                new SettingsItem(
                    "store.retention-days", "图片保留", SettingsControl.Number, Min: 1, Max: 36500,
                    Hint: "天后清理原图；文本永不清理。",
                    Keywords: ["保留", "清理", "图片", "原图", "多久删", "过期", "几天", "占用"],
                    Unit: "天",
                    Icon: "E823"),
                new SettingsItem(
                    "store.protect", "删除保护", SettingsControl.Toggle,
                    Hint: "受收藏/置顶保护的条目不参与自动清理与批量删除，也不显示删除入口；取消标记即可删除。",
                    Keywords: ["保护", "收藏", "置顶", "删除", "误删", "防手滑"],
                    Icon: "E73E"),
                new SettingsItem(
                    "store.protect-favorites", "保护收藏", SettingsControl.Toggle,
                    Parent: "store.protect",
                    Keywords: ["保护", "收藏", "星标"],
                    Icon: "E734"),
                new SettingsItem(
                    "store.protect-pinned", "保护置顶", SettingsControl.Toggle,
                    Parent: "store.protect",
                    Keywords: ["保护", "置顶", "钉住"],
                    Icon: "E718"))),

        new SettingsPage("translate", "翻译", "E774",
            "只有被翻译的文本会离开这台电脑；剪贴板历史始终只在本机。",
            new SettingsSection("translate.languages", "语言",
                new SettingsItem(
                    "service.target-language", "译文语言", SettingsControl.Choice,
                    Hint: "翻译结果使用的语言。",
                    Choices: ["中文", "英语", "日语", "韩语", "俄语", "法语", "德语", "西班牙语"],
                    Keywords: ["语言", "翻译", "目标", "译文"],
                    Icon: "E8C1"),
                new SettingsItem(
                    "service.source-language", "源语言", SettingsControl.Choice,
                    Hint: "「自动检测」适合大多数情况。",
                    Choices: ["自动检测", "中文", "英语", "日语", "韩语", "俄语", "法语", "德语", "西班牙语"],
                    Keywords: ["语言", "源", "自动"],
                    Icon: "E774")),
            new SettingsSection("translate.backend", "翻译方式",
                new SettingsItem(
                    "service.backend-kind", "翻译方式", SettingsControl.Segmented,

                    // 顺序钉在枚举值上（Relay=0、OwnKey=1）：分段存下标、设置存
                    // 枚举，两端靠声明顺序一致对上；界面上自备密钥排在前面，
                    // 由渲染顺序决定，与这里无关。
                    Choices: ["公共通道（即将推出）", "自备密钥"],
                    Hint: "自备密钥：使用你自己的 OpenAI 兼容接口与凭据，被翻译的文本直接发给你选的服务商；剪贴板历史本身不出机器。公共通道上线后被翻译的文本会经我们的中转发给模型服务，但其上线条件（自定义域名大陆可达、服务端加固、运营费用落实）尚未满足，暂不可选。",
                    Keywords: ["公共", "免费", "通道", "中继", "中转", "翻译", "后端", "密钥", "零配置", "隐私", "即将推出", "自备"],
                    Icon: "E72E"),
                new SettingsItem(
                    "service.preset", "服务商预设", SettingsControl.Custom,
                    Parent: "service.backend-kind",
                    Hint: "选中即填好服务地址与模型；手改地址或模型则视为自定义。预设附带「申请密钥」直达与「测试连接」。",
                    Keywords: ["预设", "服务商", "百炼", "阿里云", "deepseek", "智谱", "glm", "硅基流动", "kimi", "测试连接", "申请密钥", "自定义"],
                    Icon: "E713"),
                new SettingsItem(
                    "service.base-url", "服务地址", SettingsControl.Text,
                    Parent: "service.backend-kind",
                    Hint: "自备密钥时使用：OpenAI 兼容接口地址。",
                    Keywords: ["接口", "地址", "服务", "后端"],
                    Icon: "E8C1"),
                new SettingsItem(
                    "service.model", "模型", SettingsControl.Text,
                    Parent: "service.backend-kind",
                    Hint: "自备密钥时使用。",
                    Keywords: ["模型", "服务"],
                    Icon: "E8D2"),
                new SettingsItem(
                    "service.api-key", "凭据", SettingsControl.Password,
                    Parent: "service.backend-kind",
                    Hint: "自备密钥时使用。已保存的凭据不回显。填入后点「保存凭据」。",
                    Keywords: ["凭据", "密钥", "api", "key"],
                    Icon: "E72E")),
            new SettingsSection("translate.templates", "提示词模板",
                new SettingsItem(
                    "translate.templates", "提示词模板", SettingsControl.Custom,
                    Hint: "决定面板与反向输入框怎样处理当下这段文字：标准、口语、正式是翻译，换个语气；提示词优化把随手写的需求整理成给 AI 编程助手的提示词。「面板默认」管面板启动时用哪个，「输入框默认」管反向输入框。只对自备密钥的大模型生效。",
                    Keywords: ["提示词", "prompt", "模板", "口语", "正式", "优化", "vibe coding", "润色", "风格", "语气", "人设", "反向输入", "输入框默认"],
                    Icon: "E70F",
                    FullBleed: true)),
            new SettingsSection("translate.trigger", "拖选",
                new SettingsItem(
                    "hotkeys.selection-badge", "拖选后出翻译徽标", SettingsControl.Toggle,
                    Hint: "在任意应用里拖选文字后，光标旁浮现「译」徽标，点击才翻译，无需记快捷键。需要常驻全局鼠标监听（低级鼠标钩子，每一次鼠标事件都会多绕一段拾语），默认关闭；关闭即刻摘钩，拾语退出时钩子自动还给系统。与「划词翻译」热键并存，互不影响。",
                    Keywords: ["划词", "拖选", "选中", "徽标", "翻译", "鼠标", "钩子", "监听", "开销"],
                    Icon: "E721"),
                new SettingsItem(
                    "translate.hotkey-ref", "划词翻译快捷键", SettingsControl.Link,
                    Hint: "与拖选徽标是同一件事的两种触发方式；按键在「快捷键」页修改。",
                    Keywords: ["划词", "快捷键", "引用"],
                    Icon: "E765"))),

        new SettingsPage("hotkeys", "快捷键", "E765", "全局按键。点一下卡片，直接按下组合键。",
            new SettingsSection("hotkeys.all", "全局",
                new SettingsItem(
                    "hotkey.bar", "打开窄条", SettingsControl.Hotkey,
                    Hint: "唤出/收起常驻窄条。",
                    Keywords: ["窄条", "快捷键"],
                    Icon: "E77F"),
                new SettingsItem(
                    "hotkey.quickbar", "快速粘贴", SettingsControl.Hotkey,
                    Hint: "以粘贴模式呼出窄条：在插入符旁出现（取不到退鼠标），打字过滤、Enter 或编号键粘贴后消失，失焦即隐；Ctrl+Shift+B 的窄条是常驻的，用来翻看与整理。",
                    Keywords: ["快速粘贴", "粘贴", "快捷键"],
                    Icon: "E8C8"),
                new SettingsItem(
                    "winv.takeover", "也用 Win+V 呼出", SettingsControl.Toggle,
                    Parent: "hotkey.quickbar",
                    Hint: "让 Win+V 以粘贴模式呼起窄条，代替系统剪贴板面板——选一条、粘贴、消失。默认关闭；关闭即刻还原，拾语退出或被强杀时 Win+V 自动回到系统行为。与「快速粘贴」热键并存：两者都开时，Win+V 与该热键都唤起粘贴模式。其它 Win 组合键不受影响。",
                    Keywords: ["win", "winv", "接管", "系统", "剪贴板", "面板", "热键"],
                    Icon: "E765"),
                new SettingsItem(
                    "hotkey.capture", "划词翻译", SettingsControl.Hotkey,
                    Hint: "抓取当前选中的文字并翻译。",
                    Keywords: ["划词", "翻译", "快捷键"],
                    Icon: "E774"),
                new SettingsItem(
                    "hotkey.clipboard", "翻译剪贴板", SettingsControl.Hotkey,
                    Hint: "翻译剪贴板里最新的内容。",
                    Keywords: ["剪贴板", "翻译", "快捷键"],
                    Icon: "E8D2"),
                new SettingsItem(
                    "hotkey.library", "打开管理窗", SettingsControl.Hotkey,
                    Hint: "打开历史管理窗口；默认不设。",
                    Keywords: ["管理", "管理窗", "历史", "快捷键"],
                    Icon: "E8B7"),
                new SettingsItem(
                    "hotkey.reverse", "反向输入", SettingsControl.Hotkey,
                    Hint: "在当前输入框旁呼出一个小框：打中文，出英文（或按提示词模板改写），Enter 贴回原处。注意：在 Microsoft 365（Word、Excel、PowerPoint、Outlook）里 Alt+Q 是「跳到搜索框」，这是应用内快捷键，拾语注册后会悄悄把它盖掉、并不会报冲突——常用 Office 的话，在这里改键即可。",
                    Keywords: ["反向输入", "反向", "输入框", "回帖", "回复", "写英文", "翻译", "快捷键", "alt+q", "office", "搜索框"],
                    Icon: "E70F")),
            new SettingsSection("hotkeys.cheatsheet", "窗口内按键",
                new SettingsItem(
                    "hotkeys.cheatsheet", "按键速查", SettingsControl.Custom,
                    Hint: "窄条与设置窗里的按键，全部来自同一张键位表——全局键在上面改，窗口键随版本定。",
                    Keywords: ["速查", "键位", "按键", "键盘", "键帽", "快捷键", "口诀"],
                    Icon: "E765",
                    FullBleed: true)),
            new SettingsSection("hotkeys.defaults", "默认",
                new SettingsItem(
                    "hotkeys.reset", "恢复全部默认", SettingsControl.Custom,
                    Hint: "把上面的全局按键恢复成安装时的默认组合。",
                    Keywords: ["恢复", "默认", "重置", "快捷键"],
                    Icon: "E72C"))),

        new SettingsPage("about", "关于", "E946", null,
            new SettingsSection("about.app", "拾语",
                new SettingsItem(
                    "about.brand", "拾语", SettingsControl.Custom,
                    Hint: "剪贴板历史、划词翻译与悬停动作——一切只在这台电脑上。",
                    Keywords: ["品牌", "拾语"],
                    Icon: "E946"),
                new SettingsItem(
                    "about.version", "版本", SettingsControl.ReadOnly,
                    Keywords: ["版本"],
                    Icon: "E946"),
                new SettingsItem(
                    "about.update-auto", "自动检查更新", SettingsControl.Toggle,
                    Hint: "启动后悄悄查一次 GitHub Releases；发现新版本只提醒，安装永远要你亲手点。",
                    Keywords: ["更新", "升级", "版本", "检查", "github"],
                    Icon: "E72C"),
                new SettingsItem(
                    "about.update-check", "检查更新", SettingsControl.Custom,
                    Keywords: ["更新", "检查", "手动"],
                    Icon: "E72C")),
            new SettingsSection("about.help", "帮助",
                new SettingsItem(
                    "about.onboarding", "新手引导", SettingsControl.Custom,
                    Hint: "重新运行首次启动时的引导。",
                    Keywords: ["引导", "首次", "新手"],
                    Icon: "E765"),
                new SettingsItem(
                    "about.logs", "打开日志目录", SettingsControl.Custom,
                    Hint: "日志只在本机，用于排查问题。",
                    Keywords: ["日志", "log", "目录", "排查"],
                    Icon: "E8B7")),
            new SettingsSection("about.privacy", "隐私",
                new SettingsItem(
                    "about.privacy-note", "隐私说明", SettingsControl.Custom,
                    Hint: "剪贴板历史与设置只存在本机；备份是否带走密钥由你在导出时决定；只有被翻译的文本会按你的选择发送。",
                    Keywords: ["隐私", "数据", "发送"],
                    Icon: "E72E"))),
    ];

    /// <summary>
    /// 旧页 Id → 新页 Id（§5.1 落地要求）。深链只按 item Id 定位，页由
    /// <see cref="Find"/> 反查；这张表兜住还拿着旧页名跳转的调用方——
    /// 页可以改名，入口不可以断。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PageAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["record"] = "privacy",
            ["actions"] = "bar",
            ["look"] = "bar",
            ["service"] = "translate",
            ["store"] = "general",
        };

    public static SettingsItem? Find(string id)
        => Tree.SelectMany(page => page.Sections)
            .SelectMany(section => section.Items)
            .FirstOrDefault(item => item.Id == id);

    /// <summary>An item's home page in the current tree, or null for an unknown id.</summary>
    public static SettingsPage? FindPageOf(string itemId)
        => Tree.FirstOrDefault(page => page.Sections
            .Any(section => section.Items.Any(item => item.Id == itemId)));

    /// <summary>
    /// A page by id, with the alias table as the fallback for pre-redesign
    /// page ids. The single door deep links walk through when all they have
    /// is a page name.
    /// </summary>
    public static SettingsPage? ResolvePage(string pageId)
        => Tree.FirstOrDefault(page => page.Id == pageId)
           ?? (PageAliases.TryGetValue(pageId, out var aliased)
               ? Tree.FirstOrDefault(page => page.Id == aliased)
               : null);
}
