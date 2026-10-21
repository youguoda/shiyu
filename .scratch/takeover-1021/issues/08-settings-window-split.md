# 08 — 拆 SettingsWindow：自定义行搬进各自的卡片类

**来源：** 接手摸底（全仓库最大的文件，2639 行）
**Status:** done（2026-10-21，行为前后对照一致；行数目标修正见文末）

**What to build:** `SettingsWindow.xaml.cs` 一个类既渲染全部设置项（按 `SettingsSchema` 的类型分发编辑器），又手写了十几个
自定义行：定期备份（约 180 行）、免费引擎状态、翻译记录、键位速查、存储用量、已保存的服务商、日志、引导、恢复默认热键……
同类的 `ExclusionRulesCard`、`PromptTemplatesCard`、`ActionsListCard` 早已是独立文件，照这个模式把自定义行搬出去，
窗口只留导航、搜索、深链与通用编辑器。**只搬家，不改行为。**

**验收：**
- [x] 搬家前后各跑一遍探针：用全量 `run-all`（163 项）对照。唯一差别是 `flow:speech-winrt-voice` 由 FAIL 变 SKIP——那是同日另一处探针修复（见票 07），与本票无关；设置相关的检查（settings / settings-front / settings-more / credential-* / content-size / scroll-lane / tooltip / auto-backup-* / log-settings）全部 PASS。
- [x] Core 2111 与平台层 26 全绿；no-silent-catch、no-hardcoded-tokens 干净；Release 构建 0 警告。
- [ ] ~~`SettingsWindow.xaml.cs` 降到 1500 行以内~~ → 实际 2639 → 1874。见文末。

## Comments

- 2026-10-21 实现。搬出去的 7 个文件（物理行）：`AutoBackupRow` 192 · `SavedProvidersCard` 123 · `StorageUsagePanel` 138 ·
  `SettingsButtonRows` 133（关于页的品牌头 / 检查更新 / 日志 / 重新运行引导 / 隐私说明，数据页的导出导入备份）·
  `TranslationLogRow` 105 · `KeyMapCard` 104 · `FreeEngineStatusRow` 49。代码逐行照搬；窗口私有成员换成显式参数：
  `store`、`Func<AppShell?>`（壳在建页之后才接上，上屏与点击时才取，与原来同义）、owner 窗口、`ErrorOn(item)`
  （报错那一刻才找卡，与原来 `CardOf(...)?.ShowError` 同义）。已保存的服务商的「切换」「删除」仍由窗口做——凭据编辑器
  也用「删除」；`SettingsProbes` 读列表的那一行跟着改成 `_savedProviders.Element`。
- 探针没覆盖的三块（键位速查、存储用量、关于页按钮）用深链开设置窗截图看过，渲染正常。
- **行数目标修正**：1500 是开票时的估计。剩下的 1874 行是导航、搜索、深链、通用编辑器（开关 / 分段 / 数字 / 文本 /
  热键录制 / 目录 / 凭据）以及与服务商预设、凭据紧耦合的表单接线——按本票的范围本就该留在窗口里。再往下拆就是重新
  设计编辑器的接缝，不是搬家；要做另开票。
- 顺带看到但没动：`App.DebugProbes.cs:533` 有一条 Debug 构建的 CS8602（贴边隐藏探针，本票之前就有，Release 不编译它）。
