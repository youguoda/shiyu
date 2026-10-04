namespace Shiyu.App;

/// <summary>
/// 功能模块的集合与装配（O-40 拆自 App.xaml.cs）：创建、按原单体应用函数
/// 的次序订阅各模块的 Apply、接线跨模块中转位。宿主拿到的就是一个装好
/// 的模块集，OnExit 按序逐个 Shutdown。
/// </summary>
internal sealed class AppModules
{
    public required SelectionModule Selection { get; init; }
    public required TranslationModule Translation { get; init; }
    public required ReverseInputModule ReverseInput { get; init; }
    public required SettingsModule Settings { get; init; }
    public required BarModule Bar { get; init; }
    public required HotkeyModule Hotkeys { get; init; }
    public required LibraryModule Library { get; init; }
    public required UpdateModule Update { get; init; }
    public required RetentionModule Retention { get; init; }

    /// <summary>
    /// The one apply path (O-20), 拆到各模块的 Apply：订阅序即原单体里的
    /// 应用序——主题（Connect 内先行）、窄条、面板、排除名单与录制开关、
    /// 热键与钩子（数据位置搬迁的提示在 Connect 内压轴）。此前三条各自
    /// 为政的 apply 路径（保存、引导、还原）早已收拢于此。
    /// </summary>
    public static AppModules Attach(AppShell shell)
    {
        var modules = new AppModules
        {
            Selection = new SelectionModule(),
            Translation = new TranslationModule(),
            ReverseInput = new ReverseInputModule(),
            Settings = new SettingsModule(),
            Bar = new BarModule(),
            Hotkeys = new HotkeyModule(),
            Library = new LibraryModule(),
            Update = new UpdateModule(),
            Retention = new RetentionModule(),
        };

        modules.Selection.Attach(shell);
        modules.Settings.Attach(shell);
        modules.Library.Attach(shell);
        modules.Update.Attach(shell);
        shell.Connect();
        modules.Bar.Attach(shell);
        modules.Translation.Attach(shell);
        modules.ReverseInput.Attach(shell);
        shell.SettingsChanged += shell.ApplyPipelineSettings;
        modules.Hotkeys.Attach(shell);

        // 跨模块中转位：模块间不互相引用，经 shell 找彼此。
        shell.ToggleBar = modules.Bar.Toggle;
        shell.ShowQuickPaste = modules.Bar.ShowQuickPaste;
        shell.TranslateSelection = modules.Selection.TranslateSelection;
        shell.TranslateClipboard = modules.Selection.TranslateClipboard;
        shell.ShowReverseInput = modules.ReverseInput.Show;
        shell.ShowPanel = modules.Translation.ShowPanel;
        shell.KeepTranslation = (original, translated) => modules.Translation.KeepTranslation(original, translated);
        shell.ShowBadge = modules.Selection.ShowBadge;
        shell.DragSelected = modules.Selection.OnDragSelected;
        shell.OpenSettingsAt = modules.Settings.ShowAt;
        shell.ShowLibrary = modules.Library.Show;
        shell.ShowSettings = modules.Settings.Show;
        shell.ShowUpdateWindow = modules.Update.ShowWindow;
        shell.BuildStreamingModel = modules.Translation.BuildStreamingModel;

        return modules;
    }
}
