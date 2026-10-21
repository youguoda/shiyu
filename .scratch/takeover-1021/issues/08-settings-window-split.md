# 08 — 拆 SettingsWindow：自定义行搬进各自的卡片类

**来源：** 接手摸底（全仓库最大的文件，2639 行）
**Status:** ready-for-agent

**What to build:** `SettingsWindow.xaml.cs` 一个类既渲染全部设置项（按 `SettingsSchema` 的类型分发编辑器），又手写了十几个
自定义行：定期备份（约 180 行）、免费引擎状态、翻译记录、键位速查、存储用量、已保存的服务商、日志、引导、恢复默认热键……
同类的 `ExclusionRulesCard`、`PromptTemplatesCard`、`ActionsListCard` 早已是独立文件，照这个模式把自定义行搬出去，
窗口只留导航、搜索、深链与通用编辑器。**只搬家，不改行为。**

**验收：**
- [ ] 搬家前后各跑一遍设置相关探针（settings / settings-front / settings-more / credentials / content-size / scroll-lane / tooltip / auto-backup），结果一致。
- [ ] Core 与平台层测试全绿；no-silent-catch、no-hardcoded-tokens 干净。
- [ ] `SettingsWindow.xaml.cs` 降到 1500 行以内，每个搬出去的类能单独读懂。

## Comments
