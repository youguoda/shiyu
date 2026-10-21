# 拾语测试追溯矩阵（test-map）

> 总纲：[`test-plan.md`](test-plan.md)。**每张票合入时改这里**：§2 的格子只写文件名或检查名；§3 的缺口做掉了改状态、不删行；
> §4 预算、§5 人工核心回归、§6 每版指标各追加一行。
> 基线：2026-10-16，`master @ 8eb1b90`（v0.16.1）。

---

## 1. 列的含义

| 列 | 含义 | 格子里写什么 |
|---|---|---|
| L1 | Core 单元测试 `tests/Shiyu.Core.Tests` | `XxxTests` 文件名（省略 `.cs`） |
| L2 | 平台层测试 `tests/Shiyu.Windows.Tests` | 同上 |
| L3 | App 组件测试 `tests/Shiyu.App.Tests`（待建） | 同上 |
| L4 | 界面探针 `tools/probes/probe-*.ps1` | 脚本名（省略 `probe-`）或检查名前缀 |
| L5 | 场景探针（虚拟机 / 无人在场） | 脚本名 |
| L6 | 外部契约 | 脚本名 / 定时任务 |
| L7 | 安装、升级、更新 | 脚本名 / 流水线步骤 |
| L8 | 性能与可靠性 | 脚本名 / 预算编号 |
| L9 | 人工验收 | `manual-test-vN` 的项编号 |
| 缺口 | `G-xx`，详见 §3 | 编号 |

---

## 2. 区域 × 层

### REC · 记录管道

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| ClipboardPipelineTests · ContentClassifierTests · SubtypeClassifierTests · SubtypeStoreTests · ClipboardHtmlTests · FormattedEntryTests · FileEntryTests · ImageEntryTests · PngSizeTests · RetentionTests · SourceIconTests · BackfillTests · EntryStoreConcurrencyTests · StorageScaleTests | — | — | source-icons · bar-sharp · preview-image | capture-restore（"别的程序写完就占住 700 ms"场景） | — | — | storage-bench | v5（记录与来源各项） | G-04 · G-18 · G-24 |

### PRIV · 隐私、排除、凭据

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| ExclusionTests · CaptureGateTests · ClipboardWriteGateTests · LogTests（公开签名无 string）· SecretProtectionTests · StoredKeyGateTests · KeyOriginTests · SavedProvidersTests · BackupTests（剥密 / 加密）· UserDataRemovalTests · UpdateSignatureTests | （DpapiSecretProtector 仅被引用） | — | credentials（`credential-disk` 明文为 False）· tray（`tray-has-no-bar-row` 菜单无剪贴板内容） | — | — | smoke-installer `-DestructiveChecks`（删数据）· scan-release-overrides | — | v6 A1–A10 | G-04（排除标记真剪贴板）· G-16（出网白名单） |

### PLAT · 取词与剪贴板借还

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| SelectionCaptureTests · SelectionDebtTests · CaptureRestoreTests · ClipboardRestoreTests · RestoreQueueTests · SelectionDragTests · SelectionBadgeFilterTests · ScopedHoldTests（`FakeCapturePlatform`：被占重试、格式往返、排除标记、序列号） | — | — | badge | capture-restore（6 场景：淡出时被占 700 ms、用户自己 Ctrl+C、截图不借、热键连取 8 次、写完就占、全屏置顶蒙版） | — | — | — | v6 D9；0.16.1 修复项尚无清单（v7 待收编） | G-04 · G-06 · G-14（取词→面板预算） |

### HOOK · 钩子与热键

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| WinVFilterTests · HotkeyPlanTests · KeyMapTests | HotkeyRegistryTests · HotkeyScopeTests · FullScreenOverlayTests | — | tray（加速键列） | — | — | — | — | v6 D4（改键）；Win+V 卡住（0.15.0）无清单项 | G-01 · G-06 |

### BAR · 窄条

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| BarKeyboardTests · BarPlacementTests · BarRefreshPolicyTests · HoverActionTests（40%）· KeyMapTests · PreviewPlacementTests · PreviewPolicyTests · PreviewSizingTests · PreviewTeachingTests · CardImageFitTests · EmptyStateTests · HistoryBrowserTests · HistoryFilterTests · HistoryGroupsTests · EntryStoreQueryTests · TagsAndPinningTests · EntryGroupTests · FavoritesNotesUsageTests · DeleteProtectionTests · ContentRampTests | — | （BarCards 测量，待 G-08） | bar · bar-newest · bar-sharp · bar-pin · quickbar · preview · preview-image · caret · tooltip · scroll-lane · content-size · source-icons | — | — | — | measure-cold-summon · storage-bench | v5（窄条各项） | G-09（HoverActions）· G-14 · G-15 · G-20 · G-25（A6、B1–B2、H1–H6） |

### LIB · 管理窗与翻译记录

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| LibraryKeyboardTests · TranslationLogTests · TranslationHistoryTests · AgentActionTests · HistoryFilterTests | — | — | library（含 `a11y:library-command-names`）· translation-log（`flow:log-*`） | — | — | — | — | v6（翻译记录项待收编）· v5（管理窗各项） | G-25（D4–D17）· G-15 |

### PANEL · 翻译面板、词典、流式

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| TranslationTests · TranslationSessionThreadingTests · StreamingBackendTests · StreamingTimeoutTests · TranslationEchoTests · TranslationUserErrorTests · SentenceAlignTests · DictionaryApiTests · DictionaryWordTests · FallbackDictionaryTests · LanguageGuessTests · LanguageDisplayTests · MarkdownTextTests（88%）· RenderCadenceTests · BadgePlacementTests · TemplatePromptTests · StandardPromptPinTests · PromptTemplateCustomTests · PromptTemplateEditTests · PromptTemplateLibraryTests · TemplateSessionTests · ProviderPresetTests · FreeEngineBackendTests · FreeEngineChunkerTests · FreeEngineErrorTests · FreeEngineLanguagesTests · FreeEngineSettingsTests · BingWebTranslatorTests（fixture 页面）· TransmartTranslatorTests · RelayBackendTests（77%） | — | （MarkdownBox，待 G-08） | panel（含 `a11y:panel-*`）· panel-templates · markdown · stream-jank（`perf:stream-*`）· badge · translation-log | — | free-engines（直连 / 代理） | — | stream-jank 预算（已达标） | v6 B1–B21 · C1–C11 | G-08 · G-09（RelayBackend、OpenAiCompatibleBackend、LlmDictionaryApi）· G-10 · G-13 · G-25（C2、C6） |

### REV · 反向输入框与回贴

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| ReverseInputSessionTests · ReverseInputRunnerTests · ReverseInputPlacementTests · ReverseInputTemplateTests · ReverseDirectionTests · ReversePasteTests · ForegroundReclaimTests · ClipboardRestoreTests · RestoreQueueTests | （WindowsReversePasteClipboard 仅被引用） | — | reverse-select · reverse-enter · markdown（reverse 侧）· translation-log（`flow:log-reverse`） | reverse（真记事本回贴） | — | — | — | v6 D1–D12 | G-05（提权）· G-05 场景 · 目标应用矩阵（test-plan L5） |

### AGENT · AI 动作与批量翻译

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| AgentActionTests · TranslationHistoryTests（批量） | — | — | — | — | — | — | — | v6 C8 | G-25（D13 AI 子菜单、无密钥灰态） |

### SET · 设置、引导、主题、字号

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| AppSettingsTests · SettingsStoreTests（84%）· SettingsSchemaTests · SettingsSearchTests · SettingsSupportTests · OnboardingGatingTests · FreeEngineSettingsTests · DesignTokenTests（WCAG 对比度）· MotionTokenTests · ContentRampTests · TextTokenHygieneTests · ZBandTests · ThemeFollowPolicy（仅引用） | — | （SettingsBindings，待 G-07） | settings · settings-front · credentials · content-size · scroll-lane · tooltip | — | — | — | — | v6 D4–D5 · C1–C4、C10（引导） | G-07 · G-09（SettingsStore）· G-11（旧设置 fixture）· G-25（E7–E12、F1–F7） |

### UPD · 更新、安装、卸载

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| UpdateFeedTests · UpdateOrchestratorTests（83%）· UpdateStagingTests（60%）· UpdateApplyTests · UpdateSignatureTests · ReleasePackagingDrillTests · UserDataRemovalTests | InstallRegistrationTests | — | — | — | 更新 feed 形状（发布清单，手动） | smoke-installer（CI 每次推送 + release）· release.yml 公钥复验 | — | release-checklist（老版本升级路径实测一次） | G-01 · G-09（UpdateStaging）· G-12 · G-23 · G-11 |

### NET · 外部接口

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| BingWebTranslatorTests · TransmartTranslatorTests · DictionaryApiTests · RelayBackendTests · UpdateFeedTests · ProviderPresetTests（ConnectionProbe）· server/test（78，vitest） | — | — | — | — | free-engines | — | — | release-checklist C11 | G-13 · G-21 |

### A11Y · 可访问性

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| DesignTokenTests（对比度）· TextTokenHygieneTests | — | — | `a11y:panel-button-names` · `a11y:panel-infobar-readable` · `a11y:library-command-names` | — | — | — | — | v6 B17–B18（讲述人） | G-15 |

### PERF · 性能与常驻

| L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|
| StorageScaleTests · RenderCadenceTests · EntryStoreConcurrencyTests | — | — | stream-jank | — | — | — | storage-bench · measure-cold-summon · stream-jank | — | G-14 · G-18 · G-24 |

---

## 3. 缺口清单

> 状态：`open` / `in-progress（票号）` / `done（日期，资产名已挪进 §2）`。优先级与工作量沿用 test-plan 的定义。

| # | 缺口 | 层 | 优先级 | 量 | 状态 |
|---|---|---|---|---|---|
| G-01 | `Shiyu.Windows.Tests` 进 CI 阻断 | L2 | P0 | S | done（2026-10-21，ci.yml「平台层单测」· release.yml「平台层测试」，26 个） |
| G-02 | 覆盖率收集进 CI；`coverlet.runsettings` 剔除生成代码；`coverage-gate.ps1`（总 ≥ 90%，文件 ≥ 60%） | L0 | P0 | S | open |
| G-03 | 探针 nightly 进 CI（含 runner 上 PrintWindow / 字体的 spike；缺字体标 `SKIP(ci-no-font)`） | L4 | P0 | S–M | open |
| G-04 | 剪贴板平台测试：`WindowsClipboardWriter` 格式往返（含排除标记）、`WindowsClipboardMonitor` 被占重试、`WindowsCapturePlatform` 序列号 / owner；场景面加多格式还原 | L2 / L5 | P0 | M | open |
| G-05 | 提权目标：完整性级别判定（L2）+ 管理员记事本回贴与划词场景（L5） | L2 / L5 | P0 | M | in-progress（L2：ProcessIntegrityTests 随 G-01 进 CI，2026-10-21；L5 场景未做，票 30） |
| G-06 | 钩子线程：`LowLevelHookThread` 安装 / 失败 / 卸钩线程 / Dispose；`WinVHook` 锁屏后 Win 键状态（0.15.0 回归） | L2 / L5 | P0 | M | open |
| G-07 | `tests/Shiyu.App.Tests` 建项并覆盖无 WPF 的 8 个文件（884 行，`SettingsBindings` 100%） | L3 | P1 | S–M | open |
| G-08 | App STA 离屏组件：`MarkdownBox`、`TemplateMenu`、`ItemEditors` / `SettingsCards`、`BarCards` 测量、`InfoBar` | L3 | P1 | M–L | open |
| G-09 | 补最低覆盖：`HoverActions` 40 → 85、`UpdateStaging` 60 → 85、`RelayBackend` 77 → 90、`UpdateOrchestrator`、`OpenAiCompatibleBackend`、`SettingsStore`、`LlmDictionaryApi` | L1 | P1 | S×7 | open |
| G-10 | 属性测试与模糊：`MarkdownText`、`ClipboardHtml`、`SentenceAlign`、`StreamingSlicer`、`FreeEngineChunker`、`ServerSentEvents`、`ContentClassifier`、`LanguageGuess` | L1 | P1 | M | open |
| G-11 | 升级黄金 fixture：历史 schema 小库 + 各版本 `settings.json`；迁移后断言；改 schema / 设置字段必加 fixture | L1 / L7 | P1 | M | open |
| G-12 | 应用内更新端到端：本地假 GitHub API → 下载 → 三种 .sig → 暂存 → 终替 → 回滚 | L7 | P1 | M | open |
| G-13 | 契约定时任务 `contract.yml`：必应 / 腾讯 8 语、dictionaryapi.dev、GitHub feed 形状、5 家预设可达性；红了自动开 issue | L6 | P1 | S | open |
| G-14 | 性能预算与趋势：热呼出、冷呼出、取词→面板、启动→托盘、1 h 工作集；脚本统一输出 JSON；`trend.ps1` | L8 | P1 | M | open |
| G-15 | a11y 全窗扫描：全部可交互元素有名、Tab 顺序、焦点环像素、文本缩放 150% | L4 | P1 | M | open |
| G-16 | 出网白名单探针：探针实例经本地代理，断言只访问允许的主机 | L5 | P1 | M | open |
| G-17 | 人工核心回归清单（≈ 20 项）+ 兼容矩阵（OS × DPI × 主题 × IME × 文本缩放）；v3 / v4 退役 | L9 | P1 | S | open |
| G-18 | 24 h soak：合成复制器 + 内存 / 句柄 / GDI 趋势 + 日志零 error | L8 | P1 | M | open |
| G-19 | Stryker.NET 对 Core 高风险模块做一次变异测试评估，结果写进本表 §6 | L1 | P2 | S–M | open |
| G-20 | 六个稳定面的黄金截图 + 感知差分（遮罩时间戳），基线存本机 `%LOCALAPPDATA%` | L4 | P2 | M | open |
| G-21 | 服务端与 `RelayBackend` 共用契约 fixture（成功 + 各错误码 JSON） | L1 / server | P2 | S | open |
| G-22 | README 快捷键表、托盘菜单、键位速查与 `KeyMap` 的一致性测试 | L0 | P2 | S | open |
| G-23 | 升级矩阵（发版流水线）：zip 接管、旧安装包→新、卸载保留重装、卸载删数据 | L7 | P2 | M | open |
| G-24 | 混沌：库文件被占 / 只读 / 损坏、图片目录被删、磁盘满；不崩、有日志、有一次提示、恢复后继续 | L8 | P2 | M | open |
| G-25 | 视觉矩阵改写为断言探针并入 `run-all`：`visual-matrix.ps1` 里只拍图不断言的场景（A6、B1–B2、C2、C6、D4–D17、E7–E12、F1–F7、G、H、I）；`visual-matrix.ps1` 保留为人看图工具并入 nightly artifact | L4 | P2 | 按场景 S | open |

---

## 4. 性能预算

| 指标 | 预算 | 现状（日期） | 脚本 | 首次达标 |
|---|---|---|---|---|
| 热呼出：窄条隐藏后再呼出 → 可见 | P95 ≤ 150 ms | 215–248 ms（Debug，2026-10-16，review-1016 票 22）**超预算**。评审 F-21 的 300 ms 是「改不改轻量模式」的触发线，不是本预算 | measure-hot-summon（DEBUG 命令，Release 上暂测不了） | — |
| 冷呼出：轻量模式释放后首次 | P95 ≤ 600 ms | 748 ms（Debug，2026-10-16，票 22；10-03 为 924 ms）**超预算** | measure-cold-summon | — |
| 划词热键 → 面板可见（假后端） | P95 ≤ 400 ms | 未测 | measure-capture-to-panel（G-14） | — |
| 启动 → 托盘就绪（冷） | ≤ 1.5 s | 未测 | 日志事件时间差（G-14） | — |
| 流式：输入等待 max / p95 | ≤ 150 / ≤ 50 ms | 78 / 4 ms（2026-10-16） | probe-stream-jank | 2026-10-15 |
| 流式：最长 UI 操作 | ≤ 100 ms | 91 ms（2026-10-16） | probe-stream-jank | 2026-10-15 |
| 10 万条：启动首页 / 搜索 / 入库 / v11 升级 | 票 12 承诺值 | 见 `.scratch/shiyu-v3/issues/12` | storage-bench | 2026-10-02 |
| 工作集：空闲 1 h | ≤ 120 MB | 106 MB（12 h，2026-10-16）→ 0.17.5：168 MB、私有 294 MB（7.7 h，2026-10-21）**超预算** | measure-memory（G-14） | — |
| 24 h soak：内存增长 / 日志 error | ≤ 20% / 0 | 未测 | soak（G-18） | — |

---

## 5. 人工核心回归清单（索引）

> 正文放 `docs/manual-test-v7.md` 的「核心回归」一节（G-17）；这里只记编号与最近一次全勾的版本。

| 编号段 | 内容 | 最近全勾 |
|---|---|---|
| R1–R4 | 记：三种格式入历史；密码管理器不记；排除规则；暂停记录（若已做） | — |
| R5–R9 | 取：Ctrl+Shift+V 贴回；1–9 直贴；Space 预览；钉住贴回原窗；管理窗搜索与撤销删除 | — |
| R10–R13 | 懂：免费引擎划词；自备密钥划词；单词词典卡与朗读；逐句对照 | — |
| R14–R16 | 说：Alt+Q 中→英贴回聊天框；提示词优化两段式；提权窗口提示 | — |
| R17–R20 | 壳：设置即改即生效；备份导出导入；检查更新；深色主题一轮 + 讲述人读窄条 | — |

---

## 6. 每版指标

| 版本 | 日期 | Core 用例 / 秒 | Core 行覆盖（剔除生成） | 平台 / App 用例 | 探针 PASS / FAIL / SKIP / 秒 | 契约最近绿 | 预算达标 | 人工核心回归 | 已知问题最大挂龄 | 备注 |
|---|---|---|---|---|---|---|---|---|---|---|
| 0.16.1 | 2026-10-16 | 1963 / 7 | ≈ 94%（原始 88.5%） | 18 / 0 | 135 / 0 / 1 / 206 | 无定时 | 1 / 8 已测 | 无清单 | 7 版 | 基线 |
| 0.17.5+ | 2026-10-21 | 2111 / 8 | 未测 | 26 / 0 | 未跑（10-20 票 26 记 162 / 0 / 1） | 无定时 | 1 / 8 达标（热、冷呼出与工作集超预算） | 无清单 | — | 接手基线（本机 .NET SDK 9.0.319）：全量 Core 原有 1 个偶发红（设置保存，cd0e769 已修），连跑 5 遍全绿 |
