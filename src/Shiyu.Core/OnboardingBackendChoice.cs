namespace Shiyu.Core;

/// <summary>
/// 引导第 4 屏（翻译）对翻译方式的取舍（票 41、ADR-0013 决策 4）。纯函数，界面只管
/// 画与点；放在 Core 里是为了让"谁被预选、什么时候写入免费引擎"这两条规矩有测试。
///
/// 守的是 AppSettings 的原则：文本发往哪里，不做静默升级。所以——
/// <list type="bullet">
/// <item>真首启且还没有任何能用的翻译配置，才预选免费引擎；</item>
/// <item>重跑引导（设置 → 关于）预选的是已存的选择，存量用户一路点「下一步」不会被改道；</item>
/// <item>免费引擎只在"走完这一屏"（看过披露）或"用户亲手点了它"时才写入；选「跳过，用默认
/// 设置」的仍是自备密钥，首次翻译时由未配置引导卡提供一键切换。</item>
/// </list>
/// </summary>
public static class OnboardingBackendChoice
{
    /// <summary>这一屏打开时预选哪张卡（只有免费引擎与自备密钥两张可选）。</summary>
    public static TranslationBackendKind Preselect(AppSettings baseline, bool firstRun)
        => baseline.TranslationBackend == TranslationBackendKind.Free
            || (firstRun && !baseline.IsTranslationConfigured)
                ? TranslationBackendKind.Free
                : TranslationBackendKind.OwnKey;

    /// <summary>
    /// 离开这一屏时要写入的翻译方式；null 表示不动。
    /// </summary>
    /// <param name="selected">此刻选中的那张卡。</param>
    /// <param name="pickedExplicitly">用户是否亲手点过免费引擎卡（亲手点就是同意）。</param>
    /// <param name="completedScreen">是否是向前走完了这一屏（「下一步」）——而不是跳过或回退。</param>
    public static TranslationBackendKind? KindToWrite(
        TranslationBackendKind selected, bool pickedExplicitly, bool completedScreen)
        => selected == TranslationBackendKind.Free
            ? pickedExplicitly || completedScreen ? TranslationBackendKind.Free : null
            : TranslationBackendKind.OwnKey;
}
