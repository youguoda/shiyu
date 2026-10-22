# 拾语测试计划（全量）

- 更新：2026-10-23
- 基线：master（0.18.2 发布之后、调研报告 P0 五项探针全部落地）
- 本文覆盖**全部**四层测试：单元测试、自动化探针、性能测量、手动验收；外加发布核验
  流程。每一枚探针、每一条检查、每一项手动步骤都列全；数值为 2026-10-23 实测。

---

## 0. 总览与运行方法

### 0.1 四层结构

| 层 | 规模 | 位置 | 运行方式 |
|---|---|---|---|
| 单元测试 | 133 个文件 / 1533 个 `[Fact]`（实际执行 Core 2180 + Windows 26 例） | `tests/Shiyu.Core.Tests`、`tests/Shiyu.Windows.Tests` | `dotnet test Shiyu.sln -c Debug` |
| 自动化探针 | 48 枚进 run-all，共 **237 项检查** | `tools/probes/probe-*.ps1` | `powershell -File tools/probes/run-all.ps1` |
| 性能测量 | 2 个独立测量脚本 + 探针内 perf 预算 + bigdata 压力曲线 | `tools/probes/measure-*.ps1`、`tools/probes/probe-bigdata.ps1` | 单独运行，读数比对预算 |
| 手动验收 | 29 项（§5） | 本文档 | 人在机器前逐项过 |

### 0.2 运行前的硬性前提

1. **探针 exe 必须是工作树自己的 Debug 构建**：
   `dotnet build src/Shiyu.App/Shiyu.App.csproj -c Debug`
   探针默认找 `src/Shiyu.App/bin/Debug/net9.0-windows10.0.17763.0/Shiyu.App.exe`。
   构建失败后直接跑探针 = 用旧 exe 测新代码（真实发生过的事故）。
2. **PowerShell 5.1 运行探针脚本**（脚本 ASCII 编码，未带 BOM；不要用 pwsh 7）。
3. **探针数据目录全部在 `%TEMP%\shiyu-probe-run\<名字>`**，互不污染、用完可删；
   探针实例绝不读写真实用户数据。
4. **探针命令经 `SHIYU_PROBE_CMD` 环境变量进入应用**（`DebugOverrides` 分发），
   探针模式下热键、剪贴板监听、钩子全部关闭，与真实实例并排跑互不打扰。
5. 只杀自己启动的进程（`Stop-ProbeApp`）；截图与结果只写
   `%TEMP%\shiyu-probe-results`。

### 0.3 检查名前缀约定（读结果先看前缀）

| 前缀 | 含义 | 预期状态 |
|---|---|---|
| `flow:` | 行为正确性 | 必须 PASS |
| `perf:` | 性能预算 | 必须 PASS（红了 = 新发现的性能问题） |
| `defect:` | 已知缺陷的复现 | FAIL = 缺陷还在（修好后转绿当回归守卫） |
| `data:` / `window:` / `a11y:` / `keyboard:` | 探针的中间读数 | PASS |

run-all 的总出口码恒为 0，结论从末尾的 `TOTAL: PASS x / FAIL y / SKIP z` 表读。
SKIP = 有真人在场（光标移动）时，抢鼠标/真光标耦合的检查自动让位。

---

## 1. 单元测试（133 个文件 / 1533 个 Fact / 实际执行 Core 2180 + Windows 26 例）

命令：`dotnet test Shiyu.sln -c Debug`。最新实测（2026-10-23）：
**Core 2180 全绿 + Windows 26 全绿，0 失败**。
已知时序敏感项：`SettingsStoreTests.Concurrent_updates_on_the_same_field_are_all_counted`
在全量并发负载下偶发红，单独重跑即绿（`--filter "FullyQualifiedName~Concurrent_updates"`）。

### 1.1 全部测试文件与条数（按域分组）

**条目存储与查询（Core 数据层）**

| 文件 | 条数 | 覆盖 |
|---|---|---|
| EntryStoreQueryTests | 14 | 历史查询、分页、筛选 |
| EntryGroupTests | 11 | 分组 |
| EntryStoreConcurrencyTests | 2 | 并发读写 |
| ImageEntryTests | 12 | 图片条目（缩略图/原图/尺寸） |
| FileEntryTests | 9 | 文件条目与存活判定 |
| TagsAndPinningTests | 17 | 标签、置顶 |
| FavoritesNotesUsageTests | 6 | 收藏、备注、使用计数 |
| DeleteProtectionTests | 9 | 删除保护（收藏保护） |
| RetentionTests | 16 | 保留期清理 |
| SubtypeStoreTests | 4 | 子类型入库 |
| EntryStoreConcurrency/Query 之外：StorageScaleTests 18、BatchPasteTests 9、ExportTests 7、WinVFilterTests 11、VocabularyTests 11、HistoryBrowserTests 8、HistoryFilterTests 9、HistoryGroupsTests 4、EmptyStateTests 4、LibraryKeyboardTests 2、ClipboardPipelineTests 9、ClipboardHtmlTests 4、PngSizeTests 7、ImageEntry 相关齐全 |

**翻译与引擎**

| 文件 | 条数 | 覆盖 |
|---|---|---|
| TranslationTests | 20 | 翻译会话主流程 |
| TranslationEchoTests 6 / TranslationUserErrorTests 8 / TranslationHistoryTests 11 / TranslationLogTests 18 / TranslationSessionThreadingTests 3 | | 回显、用户错误、翻译记录、线程模型 |
| BingWebTranslatorTests 26 / TransmartTranslatorTests 7 | | 免费引擎两家网页接口的解析 |
| FreeEngineBackendTests 22 / FreeEngineChunkerTests 11 / FreeEngineErrorTests 5 / FreeEngineLanguagesTests 5 / FreeEngineSettingsTests 28 / FreeEngineStatusTests 8 | | 免费引擎的后端、分块、错误、语言表、设置、状态 |
| ProviderPresetTests 37 / SavedProvidersTests 7 | | 自备密钥的预设与已存凭据 |
| RelayBackendTests 25 | | 中转后端 |
| StreamingBackendTests 23 / StreamingTimeoutTests 5 | | 流式输出与超时 |
| DictionaryApiTests 17 / DictionaryWordTests 3 / LocalDictionaryTests 11 / FallbackDictionaryTests 8 | | 离线词典 API 与词条 |
| OcrStorageTests 5 / OcrTextTests 4 | | OCR 存储与文本 |
| SpeechVoicePickTests 3 | | 朗读音色挑选 |
| LanguageGuessTests 3 / LanguageDisplayTests 1 | | 语言猜测与展示 |

**反向输入框与提示模板**

| 文件 | 条数 | 覆盖 |
|---|---|---|
| ReverseInputSessionTests | 87 | 反向框会话状态机（最大的一块） |
| ReverseInputTemplateTests 15 / TemplateSessionTests 15 / TemplatePromptTests 23 / PromptTemplateLibraryTests 43 / PromptTemplateCustomTests 43 / PromptTemplateEditTests 10 / StandardPromptPinTests 4 | | 提示模板库、自定义、编辑、钉选 |
| ReversePasteTests 15 / ReverseDirectionTests 8 / ReverseInputPlacementTests 13 / ReverseInputRunnerTests 10 | | 回贴、方向切换、摆放、执行器 |

**取词与剪贴板**

| 文件 | 条数 | 覆盖 |
|---|---|---|
| SelectionCaptureTests 32 / SelectionDragTests 10 / SelectionDebtTests 12 / SelectionBadgeFilterTests 15 | | 拖选取词、拖拽状态、徽标过滤 |
| CaptureRestoreTests 31 / ClipboardRestoreTests 21 / ClipboardWriteGateTests 2 | | 剪贴板借用与还原、写入闸 |
| CaptureGateTests 5 / RecordingPauseTests 5 | | 取词闸与暂停记录 |

**窗口与布局（纯算术部分下沉 Core）**

| 文件 | 条数 | 覆盖 |
|---|---|---|
| PreviewPolicyTests | 28 | 悬停预览的完整开/转/收时序状态机 |
| PreviewPlacementTests 5 / PreviewSizingTests 16 / PreviewTeachingTests 4 / PreviewImageTransformTests 4 / CardImageFitTests 6 | | 预览摆放、尺寸、教学行、图片缩放算术 |
| WorkbenchChipsTests | 7 | 每形态动作芯片表 |
| BarPlacementTests 3 / BarRefreshPolicyTests 11 / BarKeyboardTests 5 / ZBandTests 2 | | 窄条摆放、刷新策略、按键路由（搜索 vs 选中）、Z 序 |
| HoverActionTests 11 / WinVFilterTests 11 | | 悬停动作与 Win+V 筛选 |
| MotionTokenTests 4 / DesignTokenTests 10 | | 动效时长与设计令牌 |
| RenderCadenceTests 6 | | 渲染节拍 |

**设置、热键、安全、更新、备份**

| 文件 | 条数 | 覆盖 |
|---|---|---|
| SettingsSchemaTests 34 / SettingsStoreTests 11 / SettingsSearchTests 8 / SettingsSupportTests 7 / AppSettingsTests 19 | | 设置模式、存储、搜索、默认值 |
| KeyOriginTests 50 / KeyMapTests 26 / HotkeyPlanTests 12 / HotkeyRegistryTests 3 / DefaultHotkeyTests 6 / HotkeyScopeTests 5 | | 热键来源、键表、计划、注册、作用域 |
| SecretProtectionTests 7 / BackupPasswordVaultTests 3 / StoredKeyGateTests 1 | | 密钥保护 |
| UpdateOrchestratorTests 13 / UpdateFeedTests 8 / UpdateSignatureTests 7 / UpdateStagingTests 7 / UpdateApplyTests 4 | | 更新编排、feed、验签、暂存、应用 |
| BackupTests 13 / AutoBackupScheduleTests 9 / BackupPasswordVaultTests 3 | | 手动/定期备份 |
| InstallRegistrationTests 4 / UserDataRemovalTests 6 / ReleasePackagingDrillTests 3 | | 安装注册、卸载删数据、发布包演练 |
| ProcessIntegrityTests 3 | | 单实例与进程完整性 |

**内容与杂项**

| 文件 | 条数 | 覆盖 |
|---|---|---|
| MarkdownTextTests 37 | | Markdown 渲染规则 |
| PromptTemplate 系（见上） | | |
| AgentActionTests 13 / AgentAction 相关 | | 托盘动作清单 |
| ContentClassifierTests 8 / ContentRampTests 6 / SubtypeClassifierTests 1 / TextTokenHygieneTests 2 / FormattedEntryTests 3 / InputReachTests 1 / AppIconTests 1 / SourceIconTests 8 / LogTests 14 / OnboardingGatingTests 5 / FullScreenOverlayTests 6（Windows 层：真 Win32 窗口不显示）等 | | 内容分类、令牌卫生、格式化条目、日志、引导门槛、全屏判定 |

**Windows 层（tests/Shiyu.Windows.Tests，26 条）**：BackupPasswordVaultTests 3、
FullScreenOverlayTests 6、HotkeyRegistryTests 3、HotkeyScopeTests 5、
InstallRegistrationTests 4、ProcessIntegrityTests 3、TrayIconImagesTests 2——
模式：真实 Win32 窗口/注册表操作，但**从不显示窗口**，可与用户正在跑的实例并存。

---

## 2. 自动化探针（run-all：47 枚 / 227 项检查）

命令：
```
dotnet build src/Shiyu.App/Shiyu.App.csproj -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File tools/probes/run-all.ps1
```
单枚：`powershell -File tools/probes/probe-<名>.ps1`。所有探针先跑
`seed.ps1`（固定 20 条合成数据：12 文本含链接/邮箱/色值/代码/长文、3 图
800×500/640×640/900×300、2 文件；探针自己的数据目录）。最新全量实测
（2026-10-23）：**PASS 226 / FAIL 0 / SKIP 1**。

### 2.1 窄条（quick paste bar）

- **probe-bar**——窄条本体：种子条目可见（页脚计数与实际渲染卡数）
  `data:footer-count / data:cards-realized`；壳圆角双描边既往 P0
  `defect:bar-double-radius`；键帽箭头裁切 `defect:bar-keycap-arrows-clipped`；
  托盘删除项的危险色 `defect:bar-tray-delete-danger-color`（用户在场时 SKIP）。
- **probe-quickbar**——快速粘贴条：种子历史在列 `data:quickbar-rows`；
  选中态不可见 `defect:quickbar-invisible-selection`；主行文字回退系统 12 DIP
  `defect:quickbar-tiny-main-text`。
- **probe-bar-newest**——窄条必回最新：三条路径（重开、轻量隐藏再呼出、
  外部写库后呼出）各自从列表底部翻回最新 `flow:bar-newest-one-page /
  flow:bar-keeps-place-in-use` + 读数两行。
- **probe-bar-pin**——常驻钉住 12 检查：默认关 `flow:bar-pin-default-off`、
  头部图钉在 `flow:bar-pin-header`、点图钉写设置 `flow:bar-pin-click-writes-setting`、
  钉住失焦不收 `flow:bar-pinned-stays-on-focus-loss`、不钉失焦即收
  `flow:bar-unpinned-hides-on-focus-loss`、钉住时再呼出不挪窝
  `flow:bar-pinned-summon-stays-put`、保持在最前 `flow:bar-always-in-front`、
  记住粘贴目标 `flow:bar-pinned-notes-return-target`、齿轮开设置页
  `flow:bar-settings-gear`、设置里取消钉住即收 `flow:bar-unpin-from-settings-hides`、
  托盘不再有常驻项 `flow:tray-has-no-bar-row`、日志在 `data:bar-pin-log`。
- **probe-bar-sharp**——窄条图片按屏清晰：过保留期的图留在缩略图
  `flow:bar-sharp-expired`；在屏的换上按屏像素解码的原图
  `flow:bar-sharp-originals`；图充满卡宽 `flow:bar-sharp-fills-screen`。
- **probe-bar-dock**——贴边隐藏 17 检查。基础四项：收起只露 3 DIP 细条
  `flow:bar-dock-strip`；鼠标进窗滑出动画展开到工作区顶 `flow:bar-dock-expand`；
  离窗收回 `flow:bar-dock-recollapse`；关开关还原 `flow:bar-dock-restore`。
  开关即时性四项：召出开着时开关到开 → 当帧收边 `flow:bar-dock-switch-on-summoned`；
  窄条在别处时开开关 → 原地不挪、托盘提示拖上去一次 `flow:bar-dock-switch-on-away`；
  钉住与贴边都关时拖到顶缘 → 设置不被误开 `flow:bar-dock-drop-leaves-settings`；
  开关到关 → 完整回工作区顶 `flow:bar-dock-switch-off`。
  拖动与落点五项：拖到顶缘松手即贴边 `flow:bar-dock-droptop`；拖离顶缘窄条仍显示且
  贴边保持开启、鼠标离开不收回 `flow:bar-dock-dropaway`；整窗拖动实时跟手
  `flow:bar-dock-livedrag`（实测 +90;+90 px）；拖动中捕获被夺（Alt+Tab/Win 键）→
  拖动即止、之后的悬停不携带窗口 `flow:bar-dock-capture-lost`；召出状态拖到顶缘
  当帧收边 `flow:bar-dock-summondrop`（实测 3 DIP）。
  召出与菜单三项：召出收边后悬停细线仍弹出 `flow:bar-dock-summon-hover`；从顶缘
  拖下松手不闪回、键帽不闪烁 `flow:bar-dock-dragdown-stays`；展开态点标签下拉/
  筛选按钮不收窄条、菜单关闭且指针在别处才收回 `flow:bar-dock-menu-keeps-bar`。
  **全程用户在场守卫**：真人在场（光标移动）时 17 项全体 SKIP——悬停/拖动检查
  会与真实光标打架。
- **probe-bar-dock-start**——重启后细边自动回位 3 检查：上次关闭时窄条停在顶缘 →
  启动即细边且不抢焦点 `flow:bar-dock-start-strip`（实测 3 DIP）；指针进细线展开
  `flow:bar-dock-start-hover`；上次停在离顶 300 DIP 的别处 → 启动不出现
  `flow:bar-dock-start-away`（贴边只作用于停在顶缘的窄条）。
- **probe-bar-slide**——细边滑出的节奏 1 检查：贴边窄条收起/滑出十次，记录每帧
  落点与每次窗口移动耗时 `perf:bar-slide-cadence`（实测 14.8 帧/次、帧隙中位
  13.4ms 最大 27ms、单次移动均 1.15ms、全程 162ms）；系统关动画时检查让位。
- **probe-bar-keys**——按键路由 5 检查（窄条非「搜索」即「选中」两种键态）：
  点卡片即进选中 `flow:bar-keys-click-picks`；选中态字母键直接动作（P 钉住）
  `flow:bar-keys-letter-acts`；打字回搜索框 `flow:bar-keys-typing-returns`；
  搜索框里 Ctrl+字母恒动作 `flow:bar-keys-ctrl-acts`；空搜索方向键进选中
  `flow:bar-keys-arrow-picks`。

### 2.2 预览（悬停预览 + 钉住工作台）

- **probe-preview**——摆放与拓扑：预览必开 `flow:preview-opens`、连接线退役
  `flow:preview-no-connector`、面板绝不压窄条 `defect:preview-bar-overlap`。
- **probe-preview-image**——按住空格看图：连发不回退缩略图
  `flow:preview-image-steady`、解码与屏框逐像素一致 `flow:preview-image-sharp`
  （实测 681×426 对 681×426）、绘制尺寸等于显示框 `flow:preview-image-size`、
  真屏无缝行 `defect:preview-image-seam`（实测 0 行）。
- **probe-preview-pin**——钉住语义：钉住、干净模式、无 force 收起被拒、
  取消钉即收 `flow:preview-pin`。
- **probe-preview-workbench**——钉住工作台 19 检查：每形态芯片表
  `flow:workbench-chip-table-text/image`、流程日志完整 `flow:workbench-log-complete`、
  整窗拖动跟手 `flow:preview-pin-drag`（实测 90;60 px）与拖后位置保持
  `flow:preview-pin-drag-persists`（0;0 漂移）；以及 13 条缺陷回归守卫（修复前
  全红、现在全绿）：叠印 `preview-stale-edit-overprint`、草稿串台
  `preview-draft-bleed`、芯片压教学行 `preview-stale-chips-overprint`、
  未钉误触发 `preview-chip-fires-unpinned`、存错文本 `preview-saveasnew-wrong-text`、
  handler 累积 `preview-handler-accumulation`、芯片点死 `preview-chip-clicks-dead`、
  跨形态误递 `preview-chip-cross-kind`、缩放残留 `preview-stale-zoom`、
  图钉过期 `preview-pin-glyph-stale`、元信息消失 `preview-meta-missing`、
  图钉反转 `preview-pin-click-inverted`、Esc 不退出 `preview-esc-no-exit`、
  双击不复位 `preview-dblclick-no-reset`。
- **probe-preview-anchor**——缩放锚点：四拍滚轮到 1.25⁴ `flow:anchor-zoom-reaches-target`、
  平移改 origin 而缩放延续 `flow:anchor-pan-moves-origin`、固定光标下图像点漂移
  `defect:preview-anchor-drift`（修复前 28.5 DIP → 修复后 0.273 DIP；预算 ≤2）。
  动真光标，用户在场 SKIP。
- **probe-preview-decode-cost**——4K 解码成本：冷首显 ≤2500ms `perf:decode-cold-4k`
  （实测 46–53ms）、暖缓存 ≤200ms `perf:decode-warm-cache`（实测 5–8ms）、
  解码期间输入针 ≤150ms `perf:decode-off-thread`（实测 44–50ms）。
- **probe-preview-churn**——显/收搅拌 30 轮：全程完成 `flow:churn-completes`、
  原图缓存不超 4 张 `flow:churn-originals-cap`、工作集增长 ≤120MB
  `perf:churn-workingset`（实测 0MB）、句柄增长 ≤500 `perf:churn-handles`（实测 0）。

### 2.3 翻译面板 / 反向输入框 / 朗读 / 词典 / OCR / 模板

- **probe-panel**——面板本体：窗口在 `window:panel-found`、按钮可达名
  `a11y:panel-button-names`、信息栏可读 `a11y:panel-infobar-readable`、
  壳圆角双描边 `defect:panel-double-radius`、自备密钥无网时的裸英文错误
  `defect:panel-raw-english-error`。
- **probe-overlay-pin**——面板与反向框的钉 3 检查：钉住穿过 Esc
  `flow:panel-pin`、第一击取消钉第二击关（同项断言）；反向框钉住穿过失焦
  `flow:reverse-pin`；**未钉的面板点别处即收** `flow:panel-autohide`。
- **probe-reverse-select**——反向框输出可选可复制 3 检查
  （`flow:reverse-output-selectable/copyable/same-line-pitch`）。
- **probe-reverse-enter**——Enter 按钮 4 检查：按钮在 `flow:reverse-enter-button`、
  点击即回车 `flow:reverse-enter-click-is-enter`、随状态换字
  `flow:reverse-enter-follows-state`、底栏放得下 `flow:reverse-enter-bar-fits`。
- **probe-panel-templates**——面板模板芯片 7 检查：芯片、列表、Esc 收、选模板、
  词卡按需、词卡芯片、改写布局（`flow:panel-template-*` 与 `flow:panel-word-*`）。
- **probe-panel-dictionary**——离线词典卡：首查即开且快
  `flow:dictionary-offline-fast`（实测首查 5.8ms、热查 0.4ms）、例句卡齐全
  `flow:dictionary-card-example`、屈折变化卡 `flow:dictionary-card-inflection`、
  星标进生词本 `flow:vocabulary-star`。
- **probe-panel-speech**——朗读 2 检查：WinRT 自带音色 `flow:speech-winrt-voice`、
  朗读中按钮变停止 `flow:speech-button-stop`（静音合成，不出声）。
- **probe-ocr**——离线 OCR 2 检查：认出指定短语 `flow:ocr-reads-the-image`
  （实测首查 59ms）、识别期间 UI 线程针 ≤150ms `flow:ocr-keeps-ui-responsive`
  （实测最长 0.9ms）。
- **probe-stream-jank**——LLM 流式不卡 UI 3 检查：整段渲染完整
  `flow:stream-renders-everything`、输入针预算 `perf:stream-input-latency`
  （max≤150 / p95≤50）、最长单次操作 ≤150ms `perf:stream-longest-ui-work`。
- **probe-markdown**——Markdown 渲染 3 检查：光标位、不吞选区、
  `flow:markdown-*` 三项。
- **probe-translation-log**——两框可拖 + 翻译记录 8 检查：面板/反向框的拖动把手
  `flow:panel-grip / flow:drag-grip`、拖后位置保持 `flow:drag-kept`、两框翻译都进
  记录 `flow:log-panel / flow:log-reverse`、记录进管理窗 `flow:log-library`、
  设置入口 `flow:log-settings`、自动清理 `flow:log-expand`。

### 2.4 设置 / 管理窗 / 托盘 / 记录与隐私

- **probe-settings**——设置窗 4 检查：窗口在 `window:settings-found`、全部控件
  有可达名 `a11y:settings-all-controls-named`、已知名称核对
  `a11y:settings-known-names`、分段控件对比度 `defect:settings-segment-contrast`。
- **probe-settings-more**——低频设置折叠 2 检查：常用在首屏、低频折叠
  `flow:settings-more-folded`；搜索/深链仍能落到折叠项 `flow:settings-more-deeplink`。
- **probe-settings-front**——托盘点击置前 4 检查：打开
  `flow:settings-front-open`、压过其他窗 `flow:settings-front-over-other-window`、
  从最小化恢复 `flow:settings-front-restores-minimized`、最大化保持
  `flow:settings-front-keeps-maximized`。
- **probe-library**——管理窗 6 检查：窗口在 `window:library-found`、种子历史成行
  `data:library-rows`、底部按钮组相撞的既往 P0 `defect:library-bottom-overlap`、
  宽度扫描相撞 `defect:library-width-sweep-overlap`、键盘删除与撤销
  `keyboard:library-delete-undo`、命令按钮可达名 `a11y:library-command-names`。
- **probe-tray**——托盘左键开设置 3 检查（`flow:tray-click-opens-settings /
  flow:tray-click-not-library / window:tray-message-window`）。
- **probe-credentials**——自备密钥 7 检查：分家存储 `flow:credential-list`、
  掩码显示 `flow:credential-masked`、按需明文 `flow:credential-reveal`、
  切换随行 `flow:credential-switch`、磁盘不落明文 `flow:credential-disk`、
  新鲜会话仍掩码 `flow:credential-fresh`、标记当前 `flow:credential-marks`。
- **probe-vocabulary**——生词本 3 检查：星三词筛选恰三
  `flow:vocabulary-filter`、库内开关列出三 `flow:vocabulary-toggle`、
  CSV 导出带 Anki 头与词条 `flow:vocabulary-csv-export`。
- **probe-auto-backup**——定期备份 4 检查：写密封档 `flow:auto-backup-written`、
  只留最新几个 `flow:auto-backup-pruned`、不动别人文件、损坏/锁文件容错
  `flow:auto-backup-locked`、手动导入 `flow:auto-backup-import`。
- **probe-source-icons**——来源图标 4 检查：开时窄条/库带图标
  `flow:source-icons-bar-on / flow:source-icons-library-on`、关时空槽不画
  `flow:source-icons-off`、键帽行 `flow:source-icons-keycap`。
- **probe-badge**——翻译徽标 2 检查：单字 `flow:badge-single-glyph`、
  圆角方块 `flow:badge-rounded-square`（渲 PNG 供人眼）。
- **probe-caret**——光标 3 检查：空框光标与占位同起点 `flow:caret-at-placeholder`、
  主题色光标 `flow:caret-is-accent`、Padding 只应用一次 `flow:padding-applied-once`。
- **probe-tooltip**——工具提示 2 检查：穿拾语样式 `flow:tooltip-styled`、
  跟主题切换 `flow:tooltip-follows-theme`。
- **probe-content-size**——内容字号 5 检查：标准/更小/更大三档跟随
  `flow:content-size-*`、控件恒 14 DIP `flow:controls-stay-14`。
- **probe-scroll-lane**——滚动条车道 5 检查：悬停不变宽
  `flow:scrollbar-no-hover-resize`、车道常在 `flow:scrollbar-lane-at-rest`、
  短列表也保持 `flow:scrollbar-lane-kept-when-short`、内容不跟着挤
  `flow:scrollbar-hover-keeps-content`、滚动器不画焦点框 `flow:scroller-no-focus-frame`。
- **probe-degraded-shell**——Win10 退化外观 1 检查：不透明底 + 描边
  `flow:degraded-shell-opaque`（渲 PNG 供人眼）。
- **probe-folder-open-locate**——文件夹的打开/定位 1 检查：目录条目解析正确
  `flow:folder-open-locate-resolves`（不真开资源管理器）。
- **coexistence**（run-all 内置）——同目录互斥：真实例在跑时 SKIP。

### 2.5 不进 run-all 的探针（碰真剪贴板 / 真鼠标，单独手动跑）

| 探针 | 为什么不进 | 手动跑法 | 检查 |
|---|---|---|---|
| probe-image-paste | 写真剪贴板（图片） | 闲时单跑；自动存/还原文本剪贴板 | `flow:image-paste-pastes-the-image`：图片卡 Enter 贴出的是图片本体 |
| probe-batch-paste | 写真剪贴板 + 驱动真记事本 | 同上 | `flow:batch-paste-order`（顺序）`flow:batch-paste-target`（贴回原窗） |
| probe-reverse | 真记事本 + 换剪贴板数秒 | 有人在场自动 SKIP（-Force 强跑） | 6 检查：输出、回贴、剪贴板还原、历史不变、可达名、窗口在 |
| probe-capture-restore | 真剪贴板竞争 | 同上 | 8 检查：借用的剪贴板在竞争下仍还原、拖选/热键取词都工作、失败不误报 |
| probe-free-engines | 走真网络（非官方接口） | **发版前必跑**：`-Route Direct` | 19 项：必应/腾讯两家的多语言往返；离线时 SKIP 不算过 |

---

## 3. 性能测量与预算

**独立测量脚本**（不判 PASS/FAIL，出 P50/P95/Max 读数）：
- `measure-cold-summon.ps1`：进程冷启动 → 窄条可见的全程毫秒数。
- `measure-hot-summon.ps1`：常驻进程里呼出到界面空闲的毫秒数（走
  `summon-timing` DEBUG 命令，轻量模式开/关各 20 次，弃首轮）。

**预算表（探针内断言，超出即红）**

| 预算 | 阈值 | 所在检查 | 最新实测 |
|---|---|---|---|
| 流式输入延迟 | max≤150ms、p95≤50ms | perf:stream-input-latency | 绿 |
| 流式最长 UI 操作 | ≤150ms | perf:stream-longest-ui-work | 绿 |
| 呼出耗时 | 记录比对（cold/hot 脚本） | measure-* | 按次记录 |
| 预览缩放/平移输入延迟 | max≤150、p95≤50 | defect:preview-zoom-jank-* | **边缘**（见下） |
| 预览变换 UI 成本 | longestOp≤150 | perf:preview-transform-ui-cost | 1–10ms 绿 |
| 渲染帧隙 | max≤150、p95≤50 | 同上段的 gap* | 17–31ms 绿 |
| 4K 冷解码 | ≤2500ms | perf:decode-cold-4k | 46–53ms |
| 4K 暖缓存 | ≤200ms | perf:decode-warm-cache | 5–8ms |
| 解码不卡 UI | ≤150ms | perf:decode-off-thread | 44–50ms |
| 搅拌工作集 | ≤120MB | perf:churn-workingset | 0MB |
| 搅拌句柄 | ≤500 | perf:churn-handles | 0 |
| OCR 识别 UI 针 | ≤150ms | flow:ocr-keeps-ui-responsive | 0.9ms |
| 离线词典首查 | 无硬阈值，记录 | flow:dictionary-offline-fast | 5.8ms |

**已知边缘（负载相关抖动，继续观察）**：图片缩放/平移的输入 p95 在全量
负载下于 46–60ms 间摆动（预算 50），空载 14–31ms。Linear 重采样 + 变换组
复用修复后已改善约 3 倍；残余与整机负载相关，不是固定回归。

---

## 4. 手动验收清单（每次发版前人肉过一遍）

自动化覆盖不了的手感与真机项。逐项：步骤 → 期望。

**贴边隐藏（6 项）**
1. 设置 → 快速粘贴 → 开「常驻钉住」+「贴边隐藏」→ 点别处收起 → 顶部只露
   一条 3 DIP 细边。
2. 鼠标顶到屏幕最上缘 → 窄条带动画滑出落到工作区顶。
3. 鼠标离开 → 窄条缩回细边。
4. 开一个全屏视频再顶上缘 → 不展开（防误触，预期行为）。
5. 把窄条拖离顶缘 → 贴边自动取消、窄条恢复常态。
6. 再把窄条拖到最上缘松手 → 当场缩成细边（热键召出的状态下也一样）。

**钉住的预览工作台（10 项）**
1. 悬停出预览 → 点图钉 → 元信息行与教学行收起，只剩干净内容。
2. 文本条目：点进正文直接改 → 改动是草稿，原条目不受影响。
3. 「复制」带走草稿；「存为新条目」进历史顶部。
4. Esc 退出编辑，留在只读展示。
5. 图片条目：滚轮缩放（朝光标）、按住拖动平移、双击复位。
6. 底部按形态出芯片：链接「打开/复制/存为新条目」、文件「打开/定位/复制路径」、
   图片「复制图片」、颜色「复制色值」——**每一枚都真的执行**。
7. 换预览另一个条目 → 不残留上一条的编辑框、芯片、缩放状态。
8. 鼠标移开、窄条收起 → 钉住的预览仍在最上；再点图钉 → 收起。
9. 预览窗按住空白处拖动 → 整窗实时跟手；拖到的位置在下次显示时保持。
10. 删除钉住的那张卡片 → 预览自动收起，图钉与元信息行在下一次预览时正常。

**翻译面板 / 反向输入框（5 项）**
1. 不钉的面板：点别处即收（翻译结算后、指针不在面板上时）。
2. 钉住：点别处不收；Esc 第一击取消钉、第二击关。
3. 反向框钉住：失焦不收。
4. 两框按住非控件处拖动 → 实时跟手（无外框）。
5. LLM 流式输出时拖滚动条、打字 → 不卡顿。

**系统标题栏窗口（2 项）**
1. 拖历史窗/设置窗标题栏 → 窗口本体实时跟手（无外框）。若出现外框：是系统
   「拖动时显示窗口内容」被关——拾语启动时会代开并托盘提示一次；也可在
   系统属性 → 性能设置 里手工勾回。
2. 最大化 → 拖回 → 窗口恢复正常尺寸与位置。

**更新（2 项）**
1. 旧版本里点「检查更新」→ 下载有进度、断点续传、验签通过后应用重启到新版。
2. 启动时若托盘提示「已为你打开拖动时显示窗口内容」→ 属预期（该设置此前被关）。

---

## 5. 发布核验流程（每次发版）

1. CHANGELOG 定版（[Unreleased] → [x.y.z] - 日期）。
2. `git push origin master && git tag -a vx.y.z -m "…" && git push origin vx.y.z`。
3. CI release 流水线自动：测试 → Release 发布 → zip 与安装包（Inno，版本钉死）
   → 各配 `.sha256` → ECDSA P-256 签名（私钥只在仓库 secret）→ 公钥复验 →
   安装包静默装/查/删数据/静默卸冒烟 → 建 GitHub Release（六个产物 +
   自动生成的说明）。**发版构建只来自 CI，不来自本地 bin。**
4. 发布后四道核验（本机执行过 0.17.2–0.17.6）：
   `releases/latest` 指向新 tag；产物六个齐全；zip SHA-256 对旁证；
   zip 内 `Shiyu.App.exe` 的 ProductVersion = 版本+提交哈希。
5. 发版前另跑：`probe-free-engines.ps1 -Route Direct`（免费引擎探针，
   离线时 SKIP 不算过）；启动后 1 小时内存读数记入
   `docs/release-checklist.md` 的表。
6. 更新通道实测：旧版本里点检查更新 → 升级路径走通（每次大版本做一次）。

---

## 6. 已知边缘与观察项

| 项 | 现象 | 状态 |
|---|---|---|
| 图片缩放输入 p95 | 负载下 46–60ms 摆动（预算 50），空载 14–31ms；大图小图都会碰 | Linear 重采样 + 变换复用已改善 3 倍；继续观察，触发即查 |
| 设置并发压测 | 全量负载下偶发红，单独复跑绿 | 已知时序敏感项 |
| 既往 defect 复现位 | 票 29 的 13 项 + 各视觉 P0 | 修复后全部转绿（2026-10-22 实测 FAIL 1 = 仅边缘项） |

## 7. 相关文档与约定

- `tools/probes/README.md`：探针编写规则（检查命名、run-all 准入、ASCII 约束）。
- `docs/release-checklist.md`：发布清单与内存读数表。
- `.scratch/review-1016/issues/`：29 张票——每项缺陷的根因与验收记录
  （票 29 = 预览工作台全量测试与修复）。
- 新增探针的五步配方与守卫规则见 §0.2 与 tools/probes/README.md。
