# 29 — 密钥只发给它所属的服务商：切换预设不再带走旧密钥

**来源：** Xtranslate 调研复核（docs/research/2026-09-27-xtranslate-analysis.md §7、§12 第 6 条）；
ADR-0011 的补充
**Blocked by:** —（11 已完成）
**Status:** ready-for-agent（修复票，不受 WIP 上限约束，见 issue-tracker.md）

**问题（2026-10-04 代码核对）：** 自备密钥只有一个格子（`AppSettings.BackendApiKey`），不记它是
为哪家服务商填的。于是：

- 在设置·服务里切换服务商预设时，`applyPreset`（SettingsWindow.xaml.cs:1087-1093）只改预设、
  地址和模型，密钥原样保留。下一次翻译，就会把 A 家的密钥作为 `Authorization: Bearer` 发到
  B 家的服务器。对方会回 401，但密钥已经交到了第三方手里。
- "测试连接"在凭据框留空时，会回落到已存的密钥（SettingsWindow.xaml.cs:1061-1064、
  OnboardingWindow.cs:686），同样把旧密钥发给新地址。
- 手动把"服务地址"改成另一家，走的也是同一条路。

DPAPI（票 11）保护的是密钥在磁盘上的样子，管不到它被发往哪里。Xtranslate 用"按服务商分格存
密钥"防的正是这件事。

**What to build:**

- **密钥绑定来源。** `AppSettings` 新增 `BackendApiKeyOrigin`：保存密钥时服务地址的
  scheme + host + port。它不是秘密，明文存。
  - 只比来源、不比路径。同一家服务商常有多个路径前缀（如 `/v1`、`/compatible-mode/v1`），
    改个路径不该让密钥失效；而不同的服务商从不共用主机。
  - 凡是保存密钥的地方，都同时写下来源：设置窗的"保存凭据"、引导里填的密钥、备份导入带回的
    密钥。
- **只有来源一致才带上密钥。** 在 Core 加 `AppSettings.KeyFor(baseUrl)`：来源一致就返回已存的
  密钥，否则返回空串。
  - `AppSettings.Backend` 改为经它取密钥。来源不一致时 `Backend.IsConfigured` 为 false，请求
    不带 Authorization 头，面板走"还没有配置"的引导卡。
  - 两处 `readForm` 里"留空 = 沿用已存的"，改为 `_baseline.KeyFor(当前表单地址)`。
  - 凡是判断"有没有自备密钥"的地方，统一走 `Backend.IsConfigured`，不再直接看 `BackendApiKey`
    字符串。`OwnKeyDictionary`（TranslationModule.cs:133）现在就是直接看字符串，要改。
- **迁移。** 旧设置文件里有密钥、没有来源时，加载后以当时的服务地址补上来源（维持现有的配对），
  下次保存时落盘。
- **界面。**
  - 来源不一致时，凭据框下方提示："已保存的密钥属于 {旧主机}，请填写 {当前服务商} 的密钥"，
    并显示该预设的「申请密钥」链接。
  - "测试连接"遇到来源不一致、凭据框又留空时，不发请求，直接给出第四种人话结果："请先填写
    这家服务商的密钥"。这不算失败。
  - 切回原来那家时，来源又一致了，已存的密钥自动恢复可用，不用重填。
- **备份。** 来源随设置一起走。`ToBackupJson(includeKey: false)` 照旧清空密钥，来源留着无害。
  导入的备份不带密钥时，保留本机的密钥和来源（票 11 的语义不变）。
- **不做（可选的后续）：** 按来源分格存多把密钥（即 Xtranslate 的按服务商密钥库），让来回切换
  不必重填。单格加来源绑定已足以堵住泄露；多格要连带改 DPAPI 落盘和备份剥密，等有真实需求
  再做。

**验收：**

- [ ] 单测：来源一致才带密钥；只改路径仍算一致；不一致时 `Backend.IsConfigured` 为 false，且
      请求不带 Authorization（用 handler 断言）
- [ ] 单测：迁移（有密钥、无来源 → 补上来源）；保存凭据时写下来源；切到别家再切回，密钥恢复
      可用
- [ ] 单测：两处 `readForm` 在来源不一致且框空时返回空密钥；测试连接给出第四种结果，且没有
      发出请求
- [ ] 单测：备份与恢复的往返——来源随设置走；不含密钥的备份恢复后，保留本机的密钥与来源
- [ ] `OwnKeyDictionary` 等处改走 `Backend.IsConfigured`（测试）
- [ ] 实机（进人工清单）：DeepSeek 预设填好密钥 → 切到智谱预设 → 翻译时出现"还没有配置"引导
      卡，而不是 401 报错
- [ ] 全量测试绿
