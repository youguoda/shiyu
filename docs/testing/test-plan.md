# 拾语测试计划（总纲）

> 版本：2026-10-16 ｜ 对象：`master @ 8eb1b90`（v0.16.1）｜ 视角：高级测试工程研究员
> 配套：[`test-map.md`](test-map.md)（追溯矩阵，**每张票合入时都要改它**）
> 相关：[`../manual-test-v6.md`](../manual-test-v6.md)（人工验收）· [`../visual-test-plan.md`](../visual-test-plan.md)（视觉矩阵）·
> [`../release-checklist.md`](../release-checklist.md)（发布清单）· [`../review/2026-10-16-feature-review.md`](../review/2026-10-16-feature-review.md)（功能评审）
>
> **读法**：§1–2 是现状与基线（数据都是本机实测）；§3 是策略；§4 是每一层的计划；**§5 是"以后怎么加新测试"的接入规范**；
> §6 CI 门禁；§7 指标；§8 落地顺序；附录是可以直接抄的模板与配置片段。

---

## 0. 一句话

现有体系在"Core 决策逻辑"上已经很强（1963 个用例、7 秒跑完、剔除生成代码后行覆盖约 94%），在"界面"上有一套
独创的探针体系（136 项，今天实跑 135 过 / 0 败 / 1 跳过）。空白集中在四处：

1. **平台层**（`Shiyu.Windows`，31 个文件只有 10 个被任何测试引用；18 个用例**不进 CI**）；
2. **App 层**（约 2.7 万行 C# 零单元测试，只靠探针从外面看）；
3. **真机场景**（取词 / 回贴 / 钩子只有手动跑的场景探针，而 0.16.1 的 7 条修复全在这一带）；
4. **时间维度**（网页接口漂移、性能趋势、长时间运行，没有任何定期运行的东西）。

本计划不推倒重来：保留四层体系与全部既有约定，把空白补成**九层、可定期运行、可按票接入**的结构。

---

## 1. 测试对象与风险画像

> 风险 = 出问题的概率 × 影响。概率的证据是 CHANGELOG 里各区域的修复密度；影响看它伤到的是数据、信任还是观感。

| 区域（test-map 的键） | 覆盖的模块 | 风险 | 证据 | 当前主要验证手段 |
|---|---|---|---|---|
| **PLAT · 取词与剪贴板借还** | `SelectionCapture` `ClipboardRestore` `RestoreQueue` `SelectionDebt` `WindowsCapturePlatform` `WindowsClipboardWriter/Monitor` | **最高** | 0.16.1 七条修复全在此（被占即崩、还原误报、截图工具收工、丢格式、撤掉用户复制）；0.13.0 Ctrl 松开；ADR-0003 自认"全应用最脆的一点" | Core 单测（`FakeCapturePlatform` 很完整）+ 场景探针 `probe-capture-restore`（手动、需无人在场） |
| **REV · 反向输入与回贴** | `ReverseInputSession/Runner` `ReversePaste` `ForegroundReclaim` `WindowsReversePasteClipboard` | 高 | 票 30 提权窗口贴不进（7 版已知）；0.10.0 前台抢回序列 | Core 单测 + 两个探针（选取、回车钮）+ 场景探针 `probe-reverse`（手动）+ 人工 D1–D12 |
| **HOOK · 低级钩子与热键** | `LowLevelHookThread` `WinVHook` `MouseDragHook` `HotkeyRegistry` `WinVFilter` | 高 | 0.15.0 Win+L 后 V 键全被当 Win+V；钩子曾与取词共用 UI 线程 | `WinVFilter` 单测；`HotkeyRegistry` 平台测试（不进 CI）；钩子线程零测试 |
| **NET · 外部接口形状** | `BingWebTranslator` `TransmartTranslator` `FreeDictionaryApi` `UpdateFeed` `ConnectionProbe` | 高（时间维度） | 非正式网页接口"一夜之间 404"的先例；dictionaryapi.dev 在部分网络不可达 | 裁剪真实页面做 fixture 的单测 + `probe-free-engines`（发版前手动） |
| **PANEL · 翻译面板与流式** | `TranslationSession` `StreamingSlicer` `RenderCadence` `MarkdownText` `SentenceAlign` 词典三条路 | 中高 | 0.16.1 长译文卡 15 s；Markdown 流式半截标记 | 单测 + 探针 `markdown` `stream-jank` `panel*`（有性能断言） |
| **UPD · 更新、安装、卸载** | `UpdateOrchestrator/Staging/Apply/Signature` `UserDataRemoval` `InstallRegistration` 安装脚本 | 中（影响极高） | 曾有 fail-open 与错误信任根（已修） | 单测（`UpdateStaging` 只有 60% 覆盖）+ CI 真装真卸 + 发布流水线复验签名；**没有端到端的"应用内更新"演练** |
| **SET · 设置、引导、热键配置** | `SettingsStore` `AppSettings` `SettingsSchema/Search` `SettingsBindings`(App) `OnboardingWindow` | 中 | 设置互相覆盖（O-20 已根治）；旧设置迁移（明文密钥 → DPAPI → 分格） | 单测充分；`SettingsBindings` 307 行在 App 层无测试 |
| **REC · 记录管道** | `ClipboardPipeline` `ContentClassifier` `SubtypeClassifier` `ClipboardHtml` `ImageArchive` `RetentionService` | 中 | 去重失效（已修）；图片发糊（已修） | 单测充分；平台层读剪贴板无测试 |
| **PRIV · 隐私与凭据** | `ExclusionPolicy` `CaptureGate` `Log`（签名无 string）`SavedProviders` DPAPI 备份剥密 | 影响最高、频率低 | 曾有划词路径不查排除名单、密钥发往别家（均已修） | 单测 12 个文件引用 `ExclusionPolicy`；探针 `credential-disk`；**没有"只出网到允许主机"的证据** |
| **BAR / LIB · 窄条与管理窗** | 键盘模型、放置、预览、悬停动作、分组标签、删除保护 | 中（观感） | 视觉缺陷多但已有探针守 | 单测 + 13 个探针 + 视觉矩阵（部分未入库） |
| **A11Y · 可访问性** | 名字、焦点、对比度、文本缩放 | 低频、高声誉 | 10-01 为零，v3 票 27 补到 73 处 | `DesignTokenTests` 对比度；探针 3 条 `a11y:` |
| **PERF · 性能与常驻** | 热路径、流式、10 万条库、内存 | 中 | 冷呼出 P95 约 0.9 s（10-03）；工作集 106 MB | `storage-bench` `measure-cold-summon` `stream-jank`，**无趋势** |

---

## 2. 现状盘点与基线

### 2.1 资产（保留，全部继续有效）

| 层 | 资产 | 规模 | 耗时 | 现在的门禁 |
|---|---|---|---|---|
| 静态检查 | `tools/checks/no-silent-catch.ps1`、`no-hardcoded-tokens.ps1`、`scan-release-overrides.ps1` | 3 | 秒 | 前两项 CI 阻断；第三项发布流水线 |
| Core 单元测试 | `tests/Shiyu.Core.Tests`：118 个文件 / 24.6k 行；`TempDatabase`（真 SQLite）、`TestClock`、`Fakes/`（取词平台、剪贴板监听、内存日志）、`Fixtures/`（裁剪的必应页面） | 1963 用例 | 7 s | CI 阻断 |
| 平台层测试 | `tests/Shiyu.Windows.Tests`：全屏覆盖判定、热键注册 / 作用域、安装登记 | 18 用例 | < 1 s | **不在 CI** |
| 服务端 | `server/test` vitest（假 DO state、可注入上游） | 78 用例 | 秒 | CI（Ubuntu）阻断 |
| 界面探针 | `tools/probes/`：`lib.ps1`（DPI 感知、PrintWindow 截图、只读 UIA、PostMessage、像素与对比度、在场检测）、`seed.ps1`（经真 `EntryStore` 播种）、`run-all.ps1` 串 25 个窗口探针 + 2 项共存检查 | 136 项 | 206 s | 本机手动；发版前跑 |
| 视觉矩阵 | `tools/probes/visual-matrix.ps1`：按 `docs/visual-test-plan.md` 的 A1–G 场景逐张截图到 `%TEMP%\shiyu-visual`；PASS 只表示"拍到了图"，判断靠人看图 | 25 场景 | 分钟 | 手动；不在 `run-all`，无定时 |
| 场景探针 | `probe-reverse`（真记事本回贴）、`probe-capture-restore`（6 个剪贴板争用场景） | 2 | 分钟 | 手动，需无人在场、无正式实例 |
| 网络契约 | `probe-free-engines`（直连 / 代理两路、每种语言一次） | 1 | 分钟 | 发布清单 |
| 安装与发布 | `tools/installer/smoke-installer.ps1`（静默装 → 查 → 删数据 → 静默卸）；`release.yml` 发布前用公钥复验签名；`ReleasePackagingDrillTests` | — | 分钟 | CI 每次推送 + 发布阻断 |
| 性能 | `tools/benchmarks/storage-bench.ps1`（10 万条：启动 / 搜索 / 入库 / v11 升级）、`measure-cold-summon.ps1`、`probe-stream-jank`（输入等待 max ≤ 150 ms、p95 ≤ 50 ms） | 3 | 分钟 | 只有 stream-jank 在 run-all 里 |
| 人工验收 | `docs/manual-test-v5.md`（35 项）、`v6.md`（54 项，按票分节）；`release-checklist.md` | 89 项 | 小时 | 票 ready-for-human → done |

### 2.2 基线（2026-10-16 本机实测）

| 指标 | 值 | 备注 |
|---|---|---|
| Core 用例数 / 耗时 | 1963 / 7 s | 1527 个测试方法，Theory 展开后 1963 |
| Core 行覆盖（coverlet 原始） | 88.5%（10462 / 11827 行） | 含 `RegexGenerator.g.cs` 2940 行生成代码 |
| Core 行覆盖（剔除生成代码） | ≈ 94%（8372 / 8887 行） | 这是该盯的数 |
| Core 分支覆盖 | 79.7% | 含生成代码 |
| 覆盖最低的文件（≥ 40 行） | `HoverActions` 40% · `UpdateStaging` 60% · `RelayBackend` 77% · `UpdateOrchestrator` 83% · `OpenAiCompatibleBackend` 84% · `SettingsStore` 84% · `LlmDictionaryApi` 85% · `MarkdownText` 88% | 其余 98 个文件 ≥ 90% |
| 平台层用例 | 18 / 全绿 / 64 ms | 本机跑的；CI 没跑 |
| 平台层未被任何测试引用的文件 | 21 / 31 | `ScreenGeometry` `WindowRects` `LowLevelHookThread` `WinVHook` `MouseDragHook` `Speech` `TrayIcon` `SingleInstance` `StartupRegistration` 等 |
| App 层单测 | 0 | 2.7 万行 C#；其中 8 个文件 884 行不引用 WPF，今天就能测（`SettingsBindings` 307、`FirstUseHints` 111、`MouseKeyHints` 101、`AppPaths` 58、`UserDataCommand` 49、`StartupPreference` 34 …） |
| 探针 | 136 项：135 PASS / 0 FAIL / 1 SKIP / 206 s | SKIP 是同目录互斥检查，因正式实例在运行而有意跳过 |
| 人工清单 | v6 54 项（A 凭据 10、B 模板 21、C 免费引擎 11、D 反向输入 12） | v3 / v4 是散文，无法逐项勾 |
| 已知问题挂龄 | Markdown 贴回 4 版；提权窗口 7 版 | 见功能评审 F-02 / F-03 |

### 2.3 空白地图

- **没有定期运行的东西**：探针、网络契约、性能、场景探针全靠人记得跑。
- **平台层**：`Shiyu.Windows` 的测试不在 CI；取词、钩子、DPI 几何、朗读、托盘、单实例零测试。
- **App 层**：窗口代码只能被探针从外面看；`SettingsBindings`、`MarkdownBox`、`TemplateMenu`、`StreamRefresh` 这类"有决策"的类没有单测。
- **升级路径**：没有"旧库 + 旧设置 → 新版"的黄金 fixture 演练（`storage-bench` 只演练 v11 库）；没有应用内更新的端到端。
- **隐私证据**：有"日志不含正文"的反射测试、"密钥不落明文"的探针，但没有"进程只访问允许的主机"的证据。
- **视觉矩阵**：`visual-matrix.ps1` 已入库，但它只拍图不断言（PASS = 拍到了），不在 `run-all` 里也没有定时运行；25 个场景中只有一部分被改写成了带结构断言的 `run-all` 探针。
- **人工清单**没有一份稳定的"核心回归"：每次发版跑哪 20 项没人说得清。

### 2.4 既有约定（保留，新测试必须遵守）

- 单测名是一句英文陈述（`A_slow_application_still_gets_its_answer_in_before_the_deadline`），一个测试一个行为；修复类测试在注释里写"用户实录 yyyy-mm-dd"。
- 存储不假造：`TempDatabase` 用真 SQLite；时间用 `TestClock : TimeProvider`；平台端口用 `Fakes/`。
- 外部接口用**裁剪过的真实响应**做 fixture（`Fixtures/bing-translator-page.html`）。
- 探针脚本只用 ASCII（中文用码位拼），结果与截图只写 `%TEMP%\shiyu-probe-results`，**永不进仓库**；探针实例只用 `SHIYU_DATA_DIR` 隔离目录，不注册热键；用户在场、正式实例在跑、剪贴板里是图片或文件时整条 `SKIP`。
- 探针检查名带前缀：`flow:` 行为、`defect:` 已知缺陷复现（修前红）、`perf:` 性能、`data:` 数据、`a11y:` 可访问性、`coexistence:` 共存。
- 每张票合入时把需要人判断的项补进 `docs/manual-test-vN.md`；`ready-for-human` 超过 5 张不开新功能票。

---

## 3. 策略：九层与门禁

| 层 | 名称 | 回答的问题 | 门禁 | 时长预算 |
|---|---|---|---|---|
| L0 | 静态与架构约束 | 代码有没有绕过纪律（静默 catch、硬编码令牌、Release 里的 `SHIYU_`、Core 不碰 WPF） | 推送 / PR 阻断 | 30 s |
| L1 | Core 单元测试 | 每一个决策对不对 | 阻断 | 15 s |
| L2 | 平台层测试（STA） | P/Invoke 包装在真 Windows 上对不对 | 阻断 | 1 min |
| L3 | App 组件测试 | 窗口里的决策与渲染对不对（不开真窗口，或离屏开） | 阻断 | 2 min |
| L4 | 界面探针 | 真窗口看起来、摸起来对不对 | nightly + 发版阻断 | 5 min |
| L5 | 场景探针 / E2E | 和别的程序一起用时对不对（真剪贴板、真记事本、提权、争用） | nightly（专用虚拟机）+ 发版 | 10 min |
| L6 | 外部契约 | 别人的接口今天还是那个形状吗 | nightly 定时 + 发版前 | 2 min |
| L7 | 安装、升级、更新 | 装得上、升得了、卸得净、更新链可信 | 推送（冒烟）+ 发版（升级矩阵） | 5 / 20 min |
| L8 | 性能与可靠性 | 热路径多快、内存多大、跑一天会不会坏 | nightly / 每周 | 10 min / 24 h |
| L9 | 人工验收 | 机器拍不到的（手感、IME、讲述人、多屏） | 发版 | 1–2 h |

**原则**

1. **决策下沉**：任何 `if` 都应该能在 L1 测到（O-27 的延续）。App 层只留"把决策画出来"的代码，L3 测画得对不对。
2. **先红后修**：每条用户实录先变成一个红的测试（L1 或 L4），再修；修复票的 DoD 是"那个测试绿了，并留下来"。
3. **SKIP 不是 PASS**：`SKIP` 必须带枚举过的理由；nightly 里连续 3 次 SKIP 的检查按失败处理。
4. **真的不假造**：存储用真 SQLite、接口用真响应、界面用真窗口；只假造"别人的程序"（后端、取词平台、上游）。
5. **隐私**：测试数据只能是可辨认的占位数据；截图与结果永不进仓库；探针绝不碰用户的数据目录与剪贴板（虚拟机除外）。
6. **门禁分级**：阻断的总时长 ≤ 15 min；慢的、依赖外部的放 nightly；人只做机器做不了的。
7. **每层都能接新东西**：§5 规定了每一种票把测试放在哪一层、叫什么、在哪注册。

---

## 4. 分层计划

每层按"目的 → 现有 → 新增（编号 G-xx，详表见 `test-map.md` §3）→ 出口标准"写。

### L0 · 静态与架构约束

- **现有**：三个检查脚本；Core 的 `net9.0` TFM 让"Core 引用 WPF"编译不过；`LogTests` 用反射钉住 `Log` 的公开签名没有 `string` 参数；`KeyMapTests` 钉住键位表完整性。
- **新增**
  - G-22 README 的快捷键表、托盘菜单、键位速查三处与 `KeyMap` 的一致性测试（解析 README 的表格，比对 `HotkeyPlan` 默认值与 `KeyMap` 渲染）。
  - 架构测试（放进 Core.Tests，用反射）：`Shiyu.App` 里 `DllImport` 数量 ≤ 当前值（4），超出要改 `Shiyu.Windows`；`SettingsSchema` 的每个 id 在 `SettingsBindings` 有对应 case（若 `SettingsBindings` 下沉 Core，此测试随之）。
  - `coverage-gate.ps1`（见附录 B）：Core 行覆盖（剔除生成代码）< 90% 或任一文件 < 60% 即红；白名单显式列出。
- **出口**：三项都在 `ci.yml` 的 windows job 里，总时长 < 1 min。

### L1 · Core 单元测试

- **现有**：见 §2.1；覆盖 ≈ 94%。
- **新增**
  - G-09 补覆盖最低的文件：`HoverActions`（40%：各类条目的可用动作矩阵、`IsDestructive`、顺序配置回读）、`UpdateStaging`（60%：暂存区清空、半包、磁盘满、旧暂存残留）、`RelayBackend`（77%：各错误码到人话、重试与退避）、`UpdateOrchestrator`（83%：无 .sig、.sha256 对不上、清单字段缺失）、`OpenAiCompatibleBackend`（84%：推理模型只思考没译文、SSE 半行、401/429/5xx）、`SettingsStore`（84%：写盘失败回滚、隔离文件改名）。目标：全部 ≥ 85%。
  - G-10 **属性测试与模糊**（FsCheck 或手写生成器）：`MarkdownText`（任意字符串不抛、流式前缀的块树是完整文本块树的前缀）、`ClipboardHtml`（任意 CF_HTML 头不抛、Fragment 边界）、`SentenceAlign`（句对数 = 原文句数、无丢字）、`StreamingSlicer`、`FreeEngineChunker`（分块拼回等于原文、每块 ≤ 上限）、`ServerSentEvents`（任意字节流不抛）、`ContentClassifier` / `LanguageGuess`（幂等、对空白与超长稳定）。
  - G-11 **升级黄金 fixture**：`Fixtures/upgrade/` 下放各历史 schema 的小库（v1、v11、v14…，用当时的建表语句生成）与各版本的 `settings.json`（0.9.0 明文密钥、0.10.0 `BackendApiKeyOrigin`、0.13.0 分格）；测试：打开 → 迁移 → 条目、标签、分组、置顶、收藏一条不少；设置迁移后密钥归位、来源正确。每次改 schema 或 `AppSettings` 字段，必须加一个新 fixture。
  - G-21 服务端契约共享：`server/test` 与 `RelayBackendTests` 读同一组 JSON（成功、各错误码），放 `tests/Shiyu.Core.Tests/Fixtures/relay/`，服务端测试以相对路径引用。
- **出口**：覆盖门禁绿；每个 Core 文件 ≥ 60%，关键模块（§1 高风险）≥ 90%；新 fixture 有生成说明。

### L2 · 平台层测试（`tests/Shiyu.Windows.Tests`）

- **现有**：18 用例，覆盖全屏覆盖判定、热键注册、安装登记；**不在 CI**（G-01，一行 yml）。
- **约束**：真 Win32 对象需要 STA 与消息泵；用 `[StaFact]`（`Xunit.StaFact` 包）或测试内自建 STA 线程；凡写注册表、剪贴板的测试必须用注入的键路径 / 先存后还，并在正式实例运行时 `Skip`。
- **新增**
  - G-04 **剪贴板写回与读取**：`WindowsClipboardWriter` 的格式往返（文本 + HTML + RTF + 文件列表 + 三个排除标记位），读回逐格式比对；`WindowsClipboardMonitor` 被占着（另一线程 `OpenClipboard` 持有 700 ms）时的重试与不崩；`WindowsCapturePlatform` 的序列号与 owner 判定。本机运行会碰真实剪贴板：默认 `Skip`，`SHIYU_TESTS_TOUCH_CLIPBOARD=1` 或 CI 才跑。
  - G-05 **提权判定**：`IntegrityLevel.OfForeground()`（新建的小包装）对自身进程、对打不开的进程的裁决；配合 L5 的提权场景。
  - G-06 **钩子线程**：`LowLevelHookThread` 安装成功 / 失败（注入一个必失败的钩子类型）、卸钩在钩子线程上完成、Dispose 幂等、回调在泵线程；`WinVHook` 的"Win 此刻按着吗"用 `GetAsyncKeyState` 可注入函数复现 0.15.0 的锁屏场景。
  - 几何：`ScreenGeometry.ScaleForRect`、`WindowRects` 的多显示器 DPI 换算（用假的显示器表，不需要真多屏）。
  - `DpapiSecretProtector` 往返与跨用户不可解（只能断言解不开）；`StartupRegistration` 写注入的测试键；`SingleInstance` 两个互斥量名。
- **出口**：进 CI 阻断；平台层被测试引用的文件从 10 提到 ≥ 22。

### L3 · App 组件测试（新项目 `tests/Shiyu.App.Tests`）

- **目的**：窗口代码里的决策不再只能靠探针从外面看。
- **分三步**
  1. **无 WPF 的 884 行**（G-07）：`SettingsBindings`（每个 schema id 读写往返、未变更判定 `IsUnchanged/ChangedOnly`）、`FirstUseHints` / `MouseKeyHints`（第几次触发、何时不再提示）、`AppPaths`、`UserDataCommand`、`StartupPreference`。普通 `[Fact]`。
  2. **STA 离屏组件**（G-08）：`MarkdownBox`（块树 → FlowDocument 的块数与样式、流式追加不重排已结算段、选区保留）、`TemplateMenu`、`ItemEditors` / `SettingsCards`（数值带单位、说明全渲染、不截断）、`BarCards`（给定内容字号与截断旋钮下的测量高度可预测）、`InfoBar`。用 `[StaFact]`，构造控件、`Measure/Arrange`，不 `Show`。
  3. **继续下沉**（O-27）：凡在第 2 步里发现"这其实是个决策"的，搬去 Core，L3 只剩渲染。
- **出口**：项目进 CI 阻断；`SettingsBindings` 100% 覆盖；`MarkdownBox` 与 `MarkdownText` 的测试一一对应。

### L4 · 界面探针（`tools/probes/run-all.ps1`）

- **现有**：25 个窗口探针 + 2 项共存，136 项，206 s。
- **新增**
  - G-03 **进 CI nightly**：`windows-latest` 上构建 Debug → `run-all.ps1 -SkipCoexistence` → 上传 `results.txt` 与截图为 artifact → 任一 FAIL 即红。先做一次 spike 确认 runner 上 PrintWindow 可用、Segoe Fluent Icons 存在（若缺，字形类检查在 CI 标 `SKIP(ci-no-font)` 而非 FAIL）。runner 只有 100% DPI、无 IME：DPI 变体与 IME 留给 L9。
  - G-25 **把视觉矩阵变成断言**：`visual-matrix.ps1` 里尚未成为 `run-all` 探针的场景（A6 三重筛选 footer、B1–B2 预览对齐、C2 骨架、C6 极端文本、D4–D17 管理窗各态、E7–E12 设置窄宽 / 搜索 / 深链 / 录制、F1–F7 引导五屏、G 悬停专项、H 组合交互、I 深色主题变体）逐一改写成带结构断言的 `probe-<surface>-<topic>.ps1` 并登记进 `run-all`；`visual-matrix.ps1` 本身保留为"拍给人看"的工具，并入 nightly 的 artifact。优先级：H（组合）> D（管理窗）> F（引导）> 其余。
  - G-15 **a11y 全窗扫描**：每个窗口 UIA 遍历，所有可交互元素有 `Name`（模板件白名单显式列出）、Tab 顺序与视觉顺序一致、焦点元素有可见焦点环（像素断言）、文本缩放 150% 下不截断（`UISettings.TextScaleFactor` 可在探针实例里注入）。
  - G-20 **视觉基线**：对 6 个稳定面（窄条静止、窄条 Ctrl、面板句子态、面板单词态、设置常规页、管理窗默认）保存黄金截图到 `%LOCALAPPDATA%\shiyu-golden`（不进仓库），感知差分（阈值 0.5%，时间戳区域打遮罩）；差异即生成对照图供人看。结构断言仍是主，黄金图只抓"没人想到要断言的"回归。
- **出口**：nightly 绿；视觉矩阵 25 场景全部有探针；a11y 扫描零无名元素。

### L5 · 场景探针 / E2E

- **现有**：`probe-reverse`、`probe-capture-restore`（6 场景），手动、需无人在场。
- **新增**
  - 专用执行环境：一台常开的 Windows 11 虚拟机（或 self-hosted runner），无人登录桌面、无正式实例，nightly 跑这两条 + 下面的新场景；本机只在 `-Force` 下跑。
  - G-05 **提权目标**：以管理员启动记事本（虚拟机里预先授权），Alt+Q 贴回 → 断言出现提示、译文在剪贴板、剪贴板未被还原；划词热键对提权窗口 → 断言提示而非沉默。
  - G-04 的场景面：在 `probe-capture-restore` 上加"Word 风格多格式剪贴板"（文本 + HTML + RTF + 位图）取词后逐格式还原比对。
  - 目标应用矩阵（回贴）：记事本、WordPad、Chrome 地址栏、VS Code、一个 Electron 应用——各贴一次，断言第一个字不丢、无多余换行（现在只有人工 D8）。
  - 钩子场景：开着 Win+V 接管 → 模拟 Win+L 锁屏再解锁（`LockWorkStation` 在虚拟机可行）→ 按 V → 断言不呼出窄条（0.15.0 回归）。
- **出口**：nightly 在虚拟机绿；每个场景的 SKIP 理由只能是枚举值。

### L6 · 外部契约

- **现有**：`probe-free-engines`（直连 / 代理）。
- **新增**（G-13）：一个 `contract.yml` 定时任务（每日一次，Ubuntu 或 Windows 均可）跑：必应会话 + 8 种语言、腾讯 8 种语言、`dictionaryapi.dev` 一个词、GitHub `releases/latest` 形状（字段、资产三件套齐全）、5 家预设服务商的根地址可达性（不带密钥，只看 TLS 与 401 形状）。失败 → 自动开 issue（带响应摘要，不带正文）。大陆直连与海外 runner 的结果会不同：runner 跑"形状"，发版前本机跑"直连"（现有清单项保留）。
- **出口**：接口漂移在用户之前 24 小时内被发现；发布清单引用定时任务的最近一次结果。

### L7 · 安装、升级、更新

- **现有**：CI 真装真卸；发布复验签名；`ReleasePackagingDrillTests`。
- **新增**
  - G-23 **升级矩阵**（发版流水线）：① zip 解压在默认位置 → 运行安装包接管 → 数据与设置原样；② 上一个正式版安装包 → 新安装包升级 → 版本号、自启、快捷方式、数据；③ 卸载保留数据 → 重装 → 历史回来；④ 卸载删数据 → 目录干净。用 CI 的一次性机器跑，`-DestructiveChecks` 已有的守护沿用。
  - G-12 **应用内更新端到端**（Debug 构建 + `SHIYU_UPDATE_API` 指向本地假 GitHub API）：旧版探针实例 → 检查 → 下载 → 验签（正确 / 错误 / 缺失三种 .sig）→ 暂存 → 终替进程换包 → 新版本启动 → 数据原样；再演练"替换中途失败 → 回滚"。
  - G-11 的 fixture 在这里再跑一遍（真安装目录里打开旧库）。
- **出口**：发版流水线里"升级矩阵不过不发布"。

### L8 · 性能与可靠性

- **现有**：三个脚本，无趋势。
- **预算**（建议值，首轮实测后校准，写进 `test-map.md` §4）

  | 指标 | 预算 | 现状 | 测法 |
  |---|---|---|---|
  | 热呼出：窄条已构建、隐藏后再呼出 → 可见 | P95 ≤ 150 ms | 未测 | `measure-cold-summon` 加 warm 模式 |
  | 冷呼出：轻量模式释放后首次呼出 | P95 ≤ 600 ms | ≈ 900 ms（10-03） | 同上 |
  | 划词热键 → 面板可见（假后端） | P95 ≤ 400 ms | 未测 | 新 `measure-capture-to-panel` |
  | 启动 → 托盘就绪（冷） | ≤ 1.5 s | 未测 | 进程启动到 `Shell_NotifyIcon` 成功的日志事件 |
  | 流式：输入等待 | max ≤ 150 ms、p95 ≤ 50 ms | 78 / 4 ms | `probe-stream-jank`（现有） |
  | 10 万条库：启动首页 / 搜索 / 入库 | 票 12 的承诺值 | `storage-bench` | 现有 |
  | 工作集：空闲 1 h | ≤ 120 MB | 106 MB（12 h 后） | 新 `measure-memory` |
  | 24 h soak：内存增长 | ≤ 20%，日志零 `error` | 未测 | G-18 |

- **新增**
  - G-14 把以上测量脚本统一输出一行 JSON（`{metric, value, unit, build, date}`），nightly 追加到 artifact；`tools/benchmarks/trend.ps1` 画最近 30 次；超预算 10% 即红。
  - G-18 **soak**：虚拟机上跑 24 h，合成复制器每 10 s 写一次剪贴板（文本 / 图片 / 文件轮换），每小时记工作集、私有字节、句柄数、GDI 对象数，结束时断言增长与日志。
  - G-24 **混沌**：数据库文件被占（另一进程持锁）、只读、损坏头部、图片目录被删、磁盘满（小配额 VHD）——每种断言：不崩、日志有事件、托盘有一次提示、恢复后继续记录。`SHIYU_DEBUG_TICK_CRASH` 的崩溃注入保留。
- **出口**：nightly 有趋势图；预算超出即红；soak 每周一次。

### L9 · 人工验收

- **现有**：v5 / v6 按票分节，共 89 项。
- **新增**（G-17）
  - **核心回归清单**（约 20 项，发版必跑，单独一节，不随票增长）：复制三种格式各一次进历史；Ctrl+Shift+V 贴回；1–9 直贴；Space 预览；Ctrl+Shift+Z 划词（免费引擎与自备各一次）；Alt+Q 中→英贴回微信；提权窗口提示；徽标开关；暂停记录（若已做）；设置改字号即生效；备份导出 / 导入；检查更新；托盘左右键；深色主题一轮；讲述人读一遍窄条。
  - **兼容矩阵**（每个正式版至少一格，季度全覆盖）：Windows 10 22H2 / 11 23H2 / 11 24H2 × DPI 100 / 150 / 混合双屏 × 浅 / 深 × 微软拼音 / 搜狗 × 文本缩放 100 / 150。
  - v3 / v4 的散文清单退役为"历史记录"；v7 起每节首行写明"收编自哪张票、机器已覆盖哪些项"。
- **出口**：核心回归清单每次发版勾完；兼容矩阵有日期。

---

## 5. 新测试的接入规范

> 这一节是"以后怎么加"。每张票合入前对照一遍。

### 5.1 按票的类型决定测试落点（Definition of Done）

| 票的类型 | 必须有 | 视情况 | 登记到 |
|---|---|---|---|
| **功能票** | L1：每个决策一个测试；`test-map.md` 行更新；CHANGELOG | 有像素或 UIA 可观察 → L4 探针；有人才能判断的 → L9 一节；碰外部接口 → L6 一行 + fixture；碰热路径 → L8 预算行 | `test-map.md` §2 对应区域行 |
| **修复票** | 先写一个**红**的测试复现（L1 优先，否则 L4 / L5），注释写"用户实录 yyyy-mm-dd"；修后绿，测试留下 | 若根因在平台层 → L2 | `test-map.md` §2；CHANGELOG「修复」 |
| **界面票** | L4 探针（结构断言 + 截图）；浅 / 深各拍；`a11y:` 名字检查 | L3 组件测试（若有渲染决策） | `run-all.ps1` 列表；`test-map.md` |
| **性能票** | L8 预算行 + 测量脚本 + 趋势接入 | — | `test-map.md` §4 |
| **外部接口票** | 裁剪真实响应的 fixture + L1 解析测试 + L6 契约行 | — | `Fixtures/`；`contract.yml` |
| **数据 / 设置形状变更** | G-11 的新 fixture（旧形状）+ 迁移测试 | L7 升级矩阵加一格 | `Fixtures/upgrade/` |
| **安装 / 更新票** | L7 冒烟相应检查 | L7 端到端 | `smoke-installer.ps1` |

### 5.2 放在哪里、叫什么、在哪注册

| 层 | 位置 | 命名 | 注册 | 运行命令 |
|---|---|---|---|---|
| L0 | `tools/checks/<rule>.ps1` | 动词短语 | `ci.yml` windows job 一步 | 直接执行脚本 |
| L1 | `tests/Shiyu.Core.Tests/<Class>Tests.cs` | 一句英文陈述的方法名 | 自动发现 | `dotnet test tests/Shiyu.Core.Tests` |
| L2 | `tests/Shiyu.Windows.Tests/<Class>Tests.cs` | 同上；STA 用 `[StaFact]` | 自动发现 | `dotnet test tests/Shiyu.Windows.Tests` |
| L3 | `tests/Shiyu.App.Tests/<Class>Tests.cs` | 同上 | 自动发现 | `dotnet test tests/Shiyu.App.Tests` |
| L4 | `tools/probes/probe-<surface>-<topic>.ps1` | 检查名 `flow:<surface>-<topic>`（或 `defect:` `perf:` `a11y:` `data:`） | 加进 `run-all.ps1` 的 `foreach` 列表；新命令加 `App.DebugProbes.cs` 一个 `case` | `run-all.ps1` 或单跑 |
| L5 | `tools/probes/probe-<scenario>.ps1` | 同上，另加 `scenario:` 前缀 | `tools/probes/nightly-scenarios.ps1`（新） | 虚拟机 nightly |
| L6 | `tools/probes/probe-<service>.ps1` | `net:<service>-<check>` | `contract.yml` | 定时 |
| L7 | `tools/installer/*.ps1` | — | `ci.yml` installer job / `release.yml` | CI |
| L8 | `tools/benchmarks/measure-<metric>.ps1` | 输出一行 JSON | `nightly.yml` perf job；预算写 `test-map.md` §4 | nightly |
| L9 | `docs/manual-test-vN.md` 一节 | `<节字母><序号>`，列"收编自" | 票合入时 | 发版 |

### 5.3 追溯矩阵（`test-map.md`）怎么用

- §2 是"区域 × 层"的矩阵，格子里写**文件名或检查名**，不写形容词。新测试加一个名字；删测试删一个名字。
- §3 是缺口清单 `G-xx`：新发现的空白加一行；做掉的改状态并把资产名挪进 §2，不删行。
- §4 是性能预算；§5 是人工核心回归清单的索引。
- 评审票时只看两件事：这张票的行改了没有；它新增的测试在哪一层、跑多久。

### 5.4 Fixture 与数据

- 只用可辨认的占位数据（`seed.ps1` 的风格：`占位正文 01`、`probe-group-1`、程序生成的 PNG）。
- 外部响应 fixture 必须是裁剪过的真实响应，文件头写明来源 URL、抓取日期、裁掉了什么。
- 升级 fixture 必须附生成脚本或建表语句，不能只有二进制。
- 任何 fixture 里不得出现真实姓名、路径、密钥、剪贴板内容；`tools/checks/` 可加一条扫描（正则：`sk-`、`AKIA`、`C:\Users\`）。

### 5.5 SKIP 与 flake

- `SKIP` 理由只能是：`user-present`、`real-instance-running`、`clipboard-not-text`、`offline`、`ci-no-font`、`needs-vm`。其他一律算 FAIL。
- 同一检查 14 天内无代码改动而红绿交替 ≥ 2 次 → 进 `docs/testing/quarantine.md`（名字、首次日期、负责票、到期日 ≤ 14 天）；`run-all` 对隔离中的检查标 `quarantined:` 不计入 FAIL；到期未修自动回到 FAIL。
- 修 flake 的票是"验证票"，不受 WIP 上限约束（`issue-tracker.md` 既有规则）。

### 5.6 测试代码的评审清单

- 名字读出来是一句话，且只断言这一句。
- 没有 `Thread.Sleep` 等真实时间（用 `TestClock` 或可注入的等待）；探针里的等待都有超时与理由。
- 失败信息能让人不看代码就知道差在哪（`Add-Check` 的 Detail 写数字）。
- 没有假造存储、假造接口形状。
- 修复类测试引用了用户实录日期或票号。
- 跑完不留痕：临时目录、注册表测试键、剪贴板都还原。

---

## 6. CI / CD 门禁设计

| 工作流 | 触发 | 作业 | 阻断 |
|---|---|---|---|
| `ci.yml`（现有，扩） | push / PR | windows：构建 + **Core + Windows + App 三个测试项目** + 覆盖率门禁 + 三个静态检查；installer：真装真卸；server：vitest | 是，≤ 15 min |
| `nightly.yml`（新） | 每日 02:00 + 手动 | probes：Debug 构建 → `run-all -SkipCoexistence` → artifact；perf：三个测量 + 趋势；windows-tests 双镜像（`windows-2022` / `windows-2025`） | 红了开 issue，不阻断合入 |
| `contract.yml`（新） | 每日 + 手动 | L6 五项 | 红了开 issue |
| `scenarios`（新，self-hosted / 虚拟机） | 每日 | L5 场景探针 + 每周 soak | 红了开 issue |
| `release.yml`（现有，扩） | `v*` tag | 现有全部 + 升级矩阵 + 应用内更新端到端 + 引用 nightly / contract 最近一次结果 | 是 |

片段见附录 B。GitHub 托管 runner 的已知限制：DPI 只有 100%、没有 IME、字体集与 Windows 11 不同、无法提权演练——这些固定在 L9 与虚拟机。

---

## 7. 指标与报告

每个正式版在 `test-map.md` §6 追加一行；nightly 的数值以 artifact 形式保留 90 天。

| 指标 | 来源 | 今天 | 目标 |
|---|---|---|---|
| Core 用例数 / 耗时 | `dotnet test` | 1963 / 7 s | 耗时 ≤ 15 s |
| Core 行覆盖（剔除生成代码） | coverlet + gate | ≈ 94% | ≥ 90%，无文件 < 60% |
| 平台层 / App 层用例数 | 同上 | 18 / 0 | ≥ 60 / ≥ 80（W2 末） |
| 平台层被测试引用的文件 | 脚本统计 | 10 / 31 | ≥ 22 |
| 探针检查数 / 通过率 / 耗时 | `results.txt` | 136 / 99.3% / 206 s | 全部 PASS，SKIP 仅枚举理由，≤ 6 min |
| 场景探针通过率（虚拟机） | nightly | — | 100% |
| 契约探针最近一次 | contract | — | 绿，≤ 24 h |
| 性能预算达标项 | perf | 1 / 8 已测 | 8 / 8 已测且达标 |
| 人工核心回归 | 发版 | 无清单 | 20 / 20 |
| 已知问题最大挂龄 | CHANGELOG | 7 版 | ≤ 2 版 |
| flake 隔离数 | quarantine | 0 | ≤ 3，无过期 |

---

## 8. 落地顺序

| 波次 | 内容 | 工作量 | 出口 |
|---|---|---|---|
| **W1 · 把现有的接上（≈ 1 周）** | G-01 Windows.Tests 进 CI · G-02 覆盖率门禁 + runsettings · G-03 探针 nightly（含 runner spike）· G-13 契约定时 · G-07 App 无 WPF 单测 · G-17 核心回归清单 · G-09 补最低覆盖 | S×5 + M×2 | 四条定时流水线存在且绿；覆盖门禁生效 |
| **W2 · 补出血带（≈ 2 周）** | G-04 剪贴板平台测试 · G-05 提权判定 + 场景 · G-06 钩子线程 · G-11 升级 fixture · G-12 应用内更新端到端 · G-14 性能预算与趋势 · G-08 App STA 组件（第一批：`MarkdownBox`、`SettingsBindings` 已在 W1） | M×6 + L×1 | §1 前四个高风险区域每个至少有一条 L2 或 L5 证据 |
| **W3 · 持续（随票）** | G-25 视觉矩阵改写为断言探针 · G-15 a11y 扫描 · G-10 属性测试 · G-18 soak · G-24 混沌 · G-20 黄金图 · G-23 升级矩阵 · G-16 出网白名单 · G-19 Stryker 一次性评估 · G-21 / G-22 | 按票 | 指标表全部达标 |

---

## 附录 A · 模板

### A.1 Core 单测

```csharp
public sealed class <Class>Tests
{
    // 用户实录 2026-10-15：<一句话说清当时的现象>（修复类测试必写；功能类写票号）
    [Fact]
    public void <A_sentence_that_states_the_behaviour>()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));
        using var db = new TempDatabase();
        var platform = new FakeCapturePlatform { AnswersAfterPolls = 2 };
        var sut = new <Class>(/* 真 store、假平台、可控时钟 */);

        var result = sut.<Act>(/* … */);

        Assert.Equal(<expected>, result.<Observable>);
    }
}
```

### A.2 平台层 STA 测试

```csharp
public sealed class <Class>Tests
{
    [StaFact] // 真 Win32 对象；写注册表 / 剪贴板的测试还要：
    public void <Behaviour>()
    {
        if (Environment.GetEnvironmentVariable("SHIYU_TESTS_TOUCH_CLIPBOARD") != "1"
            && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null)
        {
            return; // 本机默认不碰真实剪贴板；在报告里用 Skip 而不是静默 return（xunit 需 Assert.Skip 或 SkippableFact）
        }
        using var keep = ClipboardKeeper.Save();   // 先存后还
        // …
    }
}
```

### A.3 App 组件测试（离屏）

```csharp
[StaFact]
public void Markdown_blocks_render_to_the_same_number_of_paragraphs()
{
    var box = new MarkdownBox();           // 不 Show，不进可视树
    box.Measure(new Size(420, double.PositiveInfinity));
    box.SetMarkdown("# 标题\n\n段落\n\n- 项");
    box.Arrange(new Rect(0, 0, 420, 600));
    Assert.Equal(3, box.Document.Blocks.Count);
}
```

### A.4 界面探针

```powershell
# probe-<surface>-<topic>.ps1 - <one line: what user-visible behaviour this guards> (ticket NN).
# ASCII only (see lib.ps1 header).
param([string]$Exe = '')
. (Join-Path $PSScriptRoot 'lib.ps1')
if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks
if (Test-UserPresent) { Add-Check 'flow:<surface>-<topic>' 'SKIP' 'user-present'; return (Get-Checks) }

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\<surface>-<topic>'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null
$app = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd '<cmd>' -Env @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $hwnd = Wait-ProbeWindow -ProcessId $app.Id -DipWidth 384
    Move-WindowToProbeSpot $hwnd 0
    $shot = Get-WindowShot $hwnd
    [void](Save-Shot $shot '<surface>-<topic>')
    # 结构断言：UIA 名字 / 矩形 / 像素颜色，Detail 里写数字
    Add-Check 'flow:<surface>-<topic>' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("measured {0} expected {1}" -f $got, $want)
} finally { Stop-ProbeApp $app }
Get-Checks
```

登记：`run-all.ps1` 的 `foreach ($name in @(...))` 列表加 `'<surface>-<topic>'`；若用了新的 `<cmd>`，在 `App.DebugProbes.cs` 加一个 `case`，并在 `tools/probes/README.md` 写一行。

### A.5 人工验收行

```
| E3 | <步骤，写到按哪个键、看哪里> | <预期，写到能判断对错> | 票 NN |
```

### A.6 性能预算行（`test-map.md` §4）

```
| <指标> | <预算，带分位> | <现状，带日期> | <脚本名> | <首次达标日期或空> |
```

### A.7 契约探针行

```
Emit 'PASS' 'net:<service>-<check>' ('<what was observed, numbers only>') $ms
```

---

## 附录 B · 配置片段

### B.1 `tests/coverlet.runsettings`（剔除生成代码，只算 Core）

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat Code Coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Include>[Shiyu.Core]*,[Shiyu.Windows]*,[Shiyu.App]*</Include>
          <ExcludeByAttribute>GeneratedCodeAttribute,CompilerGeneratedAttribute,ObsoleteAttribute</ExcludeByAttribute>
          <ExcludeByFile>**/*.g.cs,**/*.g.i.cs</ExcludeByFile>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

### B.2 `ci.yml` windows job 的增量

```yaml
      - name: 单测（Core / Windows / App）
        run: >
          dotnet test Shiyu.sln -c Release --no-build
          --settings tests/coverlet.runsettings
          --collect:"XPlat Code Coverage"
          --results-directory TestResults
          --logger "trx"

      # 覆盖率门禁：Core 行覆盖 ≥ 90%（剔除生成代码），任一文件 < 60% 即红；白名单在脚本里显式列出。
      - name: 覆盖率门禁
        run: powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/coverage-gate.ps1 -Results TestResults -MinLine 90 -MinFile 60
```

### B.3 `nightly.yml`（骨架）

```yaml
name: nightly
on:
  schedule: [{ cron: '0 18 * * *' }]   # UTC 18:00 = 北京 02:00
  workflow_dispatch:
jobs:
  probes:
    runs-on: windows-latest
    timeout-minutes: 30
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 9.0.x }
      - run: dotnet build src/Shiyu.App/Shiyu.App.csproj -c Debug
      - name: 视觉探针
        run: powershell -NoProfile -ExecutionPolicy Bypass -File tools/probes/run-all.ps1 -SkipCoexistence
      - name: 结果与截图
        if: always()
        uses: actions/upload-artifact@v4
        with: { name: probe-results, path: ${{ runner.temp }}/../shiyu-probe-results, retention-days: 90 }
      - name: 任一 FAIL 即红
        run: powershell -NoProfile -Command "if (Select-String -Path $env:TEMP\shiyu-probe-results\results.txt -Pattern '\sFAIL\s' -Quiet) { exit 1 }"
  perf:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 9.0.x }
      - run: powershell -NoProfile -ExecutionPolicy Bypass -File tools/benchmarks/storage-bench.ps1
      - run: dotnet build src/Shiyu.App/Shiyu.App.csproj -c Debug
      - run: powershell -NoProfile -ExecutionPolicy Bypass -File tools/probes/measure-cold-summon.ps1 -Runs 30
      # 每个脚本末尾输出一行 JSON；trend.ps1 汇总并对照 test-map.md §4 的预算
  windows-tests-matrix:
    strategy: { matrix: { os: [windows-2022, windows-2025] } }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 9.0.x }
      - run: dotnet test tests/Shiyu.Windows.Tests/Shiyu.Windows.Tests.csproj -c Release
```

### B.4 `coverage-gate.ps1`（要点）

1. 找最新的 `coverage.cobertura.xml`；按 `filename` 聚合行命中；跳过 `*.g.cs`。
2. 总行覆盖 < `MinLine` → 红；任一文件 < `MinFile` 且不在白名单 → 红；白名单写在脚本顶部数组里，每项带理由。
3. 输出一张"最低 15 个文件"的表，便于下一张票挑。

---

## 附录 C · 术语对照

| 本文 | 仓库里的叫法 |
|---|---|
| 界面探针（L4） | 视觉探针 / `tools/probes` / `run-all` |
| 场景探针（L5） | "不在 run-all 里的探针"（`probe-reverse`、`probe-capture-restore`） |
| 契约探针（L6） | `probe-free-engines`、发布清单里的"更新通道 feed" |
| 人工验收（L9） | `docs/manual-test-vN.md`、`ready-for-human` |
| 用户实录 | CHANGELOG 与测试注释里的"用户实录 yyyy-mm-dd" |
| 预算 | 票 12 的"承诺值"、`probe-stream-jank` 的阈值 |
