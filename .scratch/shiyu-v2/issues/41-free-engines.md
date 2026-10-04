# 41 — 免费引擎：零配置翻译（必应网页接口为主，腾讯交互翻译自动兜底）

**What to build:** 落实 Xtranslate 调研（docs/research/2026-09-27-xtranslate-analysis.md
§4.1–§4.3）的免费网页引擎，作为第三种翻译方式 `Free`：不注册、不填密钥、不依赖我们
部署任何服务端。主走微软必应翻译的网页接口，失败时自动改走腾讯交互翻译（TranSmart）。
用户只看到一个选项"免费引擎"，没有引擎二级选择。

## 开工前置

**D1 已定（2026-10-04，用户选方案 A）：** 新用户引导预选"免费引擎"，自备密钥作为
"更好的质量 + AI 动作"的升级路径。决策和所接受的风险记在 ADR-0013，它修订了 ADR-0009
决策 1 与 ADR-0012 #12。所接受的风险有三条：

- 非正式接口随时会坏；
- 服务条款灰色，与票 38 拒掉的 Google 端点同类；
- 被翻译的文本会发给微软或腾讯。

实现时，同步改掉以"默认自备密钥"为前提的注释与文案：

- `AppSettings.TranslationBackend` 的注释；
- 引导文案（OnboardingWindow.cs:354、:659）；
- `service.backend-kind` 的 Hint；
- 未配置引导卡的说明文字。

**WIP 闸门：** issue-tracker.md 规定 ready-for-human 超过 5 张时不开新的功能票，
2026-10-04 计数 v3 有 15 张。开工前需把验收债降到 5 张以内，或由用户明确豁免。

## 事实基线（2026-10-04 本机实测）

本机的系统代理和 `HTTP(S)_PROXY` 都指向 127.0.0.1:7890，走代理和直连的结果不一样。
能代表"没开代理的大陆用户"的是直连（curl `--noproxy '*'`）。

| 请求 | 直连结果 | 说明 |
|---|---|---|
| GET cn.bing.com/translator（取会话） | 200，0.40s | 页面约 620KB；经代理访问 www.bing.com 会跳到 cn |
| ttranslatev3 短句 → en | 200，0.8s | "我先撤了哈，明天见"→"I'm heading out for now, see you tomorrow."；响应带 `detectedLanguage`、`usedLLM:true` |
| ttranslatev3 同句 → ja | 200，1.0s | 中英以外的目标语言可用 |
| ttranslatev3 1392 字（60 条不同内容） | 200，**9.05s** | 60 条全部译出，但耗时超过 Xtranslate 设的 8s 超时 |
| ttranslatev3 坏令牌 | HTTP 200 + `{"statusCode":205}` | 205 在响应体里，不是 HTTP 状态码 |
| transmart api/imt 短句 zh→en | 200，0.21s | "I'm leaving now, see you tomorrow" |
| transmart en→ja | 200，0.35s | 中英以外的目标语言可用 |
| transmart 1392 字 | 200，3.67s | 60 条全部译出 |
| translate.googleapis.com（gtx） | **8s 超时** | 走代理时第一个请求就是 429 |

## Core：两个引擎

- 新增 `TranslationBackendKind.Free` 和 `FreeEngineBackend : ITranslationBackend`。它**不实现
  `IStreamingModel`**——它不是通用模型。内部是两个整段翻译器：`BingWebTranslator`、
  `TransmartTranslator`。
- **必应**（规格照 Xtranslate 的 microsoft.js，已对 2026-10-04 的页面核实）：
  - GET `https://cn.bing.com/translator` 并跟随跳转。翻译地址用**跳转后的主机**拼成
    `https://{host}/ttranslatev3`；主机不属于 `*.bing.com` 时退回 cn。
  - 解析三项：`params_AbusePreventionHelper = [key, "token", ttl]`（key 是毫秒时间戳；
    **ttl 的单位是毫秒**，今天是 3600000，即 1 小时）、`IG:"hex"`、**第一个** `data-iid="…"`
    （页面里有两个）。
  - 缓存到期时刻 `exp = now + max(ttl − 60s, 0)`，只在 `exp > now + 15s` 时复用；并发取会话
    共享同一个在途 Task。
  - **缓存放在后端实例之外**（进程级持有者）。`AppSettings.BuildTranslationBackend()`
    每次翻译都新建一个后端，缓存挂在实例上就等于每次翻译都重新下载一遍页面。
  - POST `{翻译地址}?isVertical=1&IG=…&IID=…`，form 字段为 `fromLang`（`auto-detect` 或映射后
    的语言码）、`to`、`text`、`token`、`key`。成功的响应是
    `[{translations:[{text,to}], detectedLanguage:{language}}]`。
  - HTTP 401，或响应体里 `statusCode: 205` → 强刷会话后重试**恰好一次**；其余 `statusCode ≠ 200`
    一律算失败。实测不需要 cookie。
- **腾讯 TranSmart**：POST `https://transmart.qq.com/api/imt`，JSON 为
  `{header:{fn:"auto_translation", client_key}, type:"plain", model_category:"normal",
  source:{lang, text_list}, target:{lang}}`。
  - `client_key = browser-chrome-130.0.0-Windows_10-{8 位随机}-{毫秒时间戳}`，每个进程生成一次。
  - `text_list` 按行切分，返回的 `auto_translation[]` 用 `\n` 拼回。空行的处理由探针核实：
    `text_list` 含空串时，返回的数组长度是否一致？不一致，就本地先剔除空行，再按原位置回填。
  - 源语言**必填**。用户指定了就映射。"自动检测"时先用探针确认它接不接受 `auto`；不接受就按
    `LanguageGuess` 的文字系统推断：汉字→zh、假名→ja、谚文→ko、西里尔→ru、拉丁→en。这样
    拉丁文字的法/德/西语会被当成英文——这是已知局限，写进注释并用测试钉住。
- **伪造的 UA/Referer 只加在这两个后端自己的 `HttpRequestMessage` 上**，绝不写进
  `HttpClients.Shared.DefaultRequestHeaders`，否则它会跟着发给所有大模型服务商。连接池照旧用
  `HttpClients.Shared`。
- **语言码表**：名字归一与 `LanguageDisplay` 共用同一套别名（english/英文/英语 …）。8 种语言
  映射到必应的 `zh-Hans/en/ja/ko/ru/fr/de/es`、腾讯的 `zh/en/ja/ko/ru/fr/de/es`。写不变量
  测试：`service.target-language` 和 `service.source-language` 的每个选项，两家都有对应的码。
  今天只实测了 zh/en/ja，其余五种由网络探针各验一条。

## Core：分块、兜底、错误

- **分块渐进输出**，取代原票的"整段返回再按句模拟流式"。长文走必应实测要 9s，整段等完
  才上屏会让面板一直空等。
  - 按段落和句末切块：首块不超过 150 字，让首字出得快；其后每块不超过 400 字。这两个都是
    常量，按探针数据调。
  - 逐块顺序翻译（需要时可改为两路并发、按序吐出），每块的译文一到就经切片器吐出。
  - 块间的分隔（换行）原样保留，拼回后要与各块译文一字不差。
  - 切片器从 `RelayBackend.SplitForStreaming` 抽出来，两边共用，不另复制一份。
  - 单块超时 8s。总长上限 5000 字（常量），超出时给人话："免费引擎单次最多翻译 5000 字，长文
    请改用自备密钥"。
- **兜底与冷却**：
  - 必应在第 k 块失败（网络、超时、解析失败、强刷后仍是 205、5xx、429）时，本次从第 k 块
    起全部改走腾讯，中途不再切回。
  - 同时把"必应不健康"记 5 分钟（用假时钟可测）。冷却期内的新翻译直接走腾讯，不让每次
    翻译都先白等 8s。
  - 用户取消永远不算失败，不触发兜底。
  - 两家都失败时抛 `TranslationFailedException("免费引擎暂时不可用：微软和腾讯都没有响应，稍后
    再试，或在 设置 → 翻译 换一种翻译方式。")`。
- **错误文案与 `TranslationUserErrorMapper` 的配合**：分类器按子串判定类别，`Auth` 类既不给
  "重试"，还会叫用户去检查 API 密钥。上面"改用自备密钥"之类的说法含"密钥"二字，会被误判成
  `Auth`。做法：在 `Classify` 里紧跟"还没有配置"那条规则之后加一条——以"免费引擎"开头的
  消息归 `Other`，保留原措辞，也保留"重试"。这条要排在超时、429、Auth、网络几条判定之前；
  `Security`（TLS）仍然最先判。内部异常照常挂上，供"复制错误详情"使用。每条免费引擎文案都
  写分类单测。
- **预热**：当前翻译方式是免费引擎时，徽标出现或翻译热键按下的那一刻，若会话缺失或快过期，
  就在后台取一次。不设定时器；失败不提示。
- 可选：用必应返回的 `detectedLanguage` 校正面板上的源语言标签。

## 设置与引导

- 设置·翻译方式的分段加第三项"免费引擎"。分段存的是下标，设置存的是枚举名：给 `Free` 定好
  下标后，同步扩展 `Each_choice_index_lands_on_its_own_enum_value`（RelayBackendTests）。
  票 36 那个阻塞级 bug 就是这里错位造成的。
- 隐私披露（写在分段的 Hint 里）："免费引擎：被翻译的文本发给微软必应翻译的网页接口，不可用时
  改发腾讯交互翻译，无需账号。这是网页接口而非正式 API，可能随时变更或限流。剪贴板历史本身
  不出机器。"
- **不加**引擎二级选择，也不加谷歌开关。依据：CONTEXT.md 说设置项越少，"小而精美"越可信；
  AppSettings 的注释要求每加一项都要能自证。
- `IsTranslationConfigured`：`Free` 恒为 true。`BuildTranslationBackend`：`Free` →
  `FreeEngineBackend`。
- 未配置引导卡（票 08）加一枚"用免费引擎"按钮。点击即
  `SettingsStore.Update(latest => latest with { TranslationBackend = Free }, …)`，然后就地重译。
  点击就是同意，没有任何静默切换。
- **面板在场时写设置会弄丢 Esc。** 每次写设置都会广播 `SettingsChanged`，`HotkeyModule` 随之
  整体重建热键注册表（HotkeyModule.cs:36-43）。面板持有的作用域 Esc 跟着旧注册表一起被注销，
  而 `HoldEscape` 里的 `_escape ??=` 不会重新挂上。这本来就是个潜伏问题：面板开着时去设置窗改
  任何一项都会触发。这枚按钮会让它变成常规路径。
  - 修法：面板在场时收到 `SettingsChanged`，就在**新**注册表上重挂 Esc。
  - 顺序有两处要求：重挂必须排在 `HotkeyModule` 重建之后；必须先释放旧作用域，再注册新的。
    新旧注册表共用同一个消息窗口，id 都从 1 开始编号，旧作用域释放得晚了，会把新注册的
    同号热键注销掉。
- 存量用户一律不动。这是 AppSettings 的原则：文本发往哪里，不做静默升级。
- 引导第 4 屏（翻译）：预选"免费引擎"卡并带上披露行，自备密钥卡紧随其后，文案写明"更好的
  质量与 AI 动作需要自备密钥"。
- `AppSettings.TranslationBackend` 的类型默认值保持 `OwnKey`（ADR-0013 决策 4）。只有走完翻译
  这一屏（看过披露）的新用户才写入 `Free`；选"跳过，用默认设置"的仍是 `OwnKey`，首次翻译时
  由引导卡提供一键切到免费引擎。
- 以下几条维持只走自备密钥，加测试钉住，防止回归：
  - Agent 动作与管理窗批量翻译：`LibraryMenus` 已按 `Backend.IsConfigured` 门控；遇到非
    `IStreamingModel` 后端时，`BuildStreamingModel` 会回落到自备密钥。
  - LLM 词典兜底（`OwnKeyDictionary`）。
  - 批量请求不得打到网页接口上。
- 票 42 的提示词模板按钮在 `Free` 下隐藏（网页翻译没有 prompt）。由票 42 的
  `PromptTemplatesApply` 判定；两票谁后落地，谁补这条测试。

## 不做

- **谷歌 gtx**：直连超时（大陆不可达），走代理时第一个请求就是 429。维持票 38 的 wontfix，
  并在票 38 的登记册里补上这条实测证据。
- **免费大模型**（智谱 GLM-4-flash、硅基流动等）：已由票 08 的服务商预设覆盖。它们要注册拿
  密钥，不属于零配置这一档。
- **有官方免费额度的机器翻译 API**（Azure 翻译、腾讯云机器翻译等）：同样要账号和密钥。应作为
  "自备密钥"下的新预设种类，另行开票。

**价值:** 新用户打开就能翻译：不注册，不填密钥，也不依赖我们部署任何东西。补上的是"没填
key，翻译就等于不存在"这块最大短板。公共通道（票 36）零配置的价值也大部分被它覆盖了。

**Blocked by:** None（34 已完成，D1 已定）。开工前置见上：WIP 闸门。

**Status:** ready-for-agent（WIP 闸门：issue-tracker.md 规定 ready-for-human 超过 5 张时不开
新的功能票，2026-10-04 计数 v3 有 15 张；开工前需降到 5 张以内，或由用户豁免）

- [x] ADR-0013 落盘（2026-10-04；ADR-0009、ADR-0012 的状态行已标注被修订）
- [ ] 以"默认自备密钥"为前提的注释与文案改为引用 ADR-0013（「开工前置」列出的四处）
- [ ] CONTEXT.md 术语表补"免费引擎"
- [ ] 必应会话测试，用真实页面裁剪出的 HTML 夹具（含两个 data-iid、毫秒 TTL）：解析；缓存
      与 15s 提前刷新（假时钟）；并发去重（N 路并发只取一次页面）；跳转后的主机→翻译地址；
      体内 205 与 HTTP 401 各强刷一次（第二次仍是 205 就报错，不循环）
- [ ] TranSmart：请求体形状、按行切分与拼回、空行保持、源语言推断（含"拉丁=英文"已知局限）
- [ ] 语言码表 + 8 种语言的不变量测试（两家 × 源语言、译文语言两个设置项）
- [ ] 分块渐进输出：切块边界、拼回逐字一致、首块小；切片器与 RelayBackend 共用
- [ ] 兜底与冷却（假时钟）；取消不触发兜底；两家都失败时给人话
- [ ] `TranslationUserErrorMapper` 加"免费引擎"前缀规则（排在 Auth 之前）；每条免费引擎文案
      都有分类单测，且保留"重试"
- [ ] 伪造的 UA/Referer 只出现在这两个后端的请求上（handler 断言；`HttpClients.Shared` 默认头
      不变）
- [ ] 设置第三项 + 下标↔枚举不变量 + 隐私披露；未配置引导卡的"用免费引擎"按钮；引导预选免费引擎
- [ ] 面板在场时写设置后 Esc 仍然有效（先释放旧作用域，再在新注册表上重挂，排在 HotkeyModule
      之后）
- [ ] `Free` 下 Agent 动作、批量翻译、LLM 词典仍只走自备密钥（测试）
- [ ] 网络探针 `tools/probes/probe-free-engines.ps1`：直连与系统代理各跑一遍；检查必应会话解析
      + 中→英一条、腾讯中→英一条、8 种目标语言各一条、TranSmart 是否接受 `auto`、空行行为；
      离线时 SKIP；输出各项耗时
- [ ] docs/release-checklist.md 加一行"免费引擎探针（直连）通过"——非正式接口坏了，要在发版
      前自己发现，而不是等用户来报
- [ ] 人工验收项进清单：全新数据目录走引导、全用默认 → 翻译一段英文直接出译文；面板里点
      "用免费引擎"后按 Esc 能关掉面板；断网时出人话；设置里的披露文案读得通
- [ ] 全量测试绿

## Comments

- **2026-10-04 重写**。依据当日实测（curl；直连 = `--noproxy '*'`）与 Xtranslate 源码核对
  （microsoft.js / tencent.js / google.js / index.js）。相对原票的更正：
  1. 原票说"两家均只支持中英对"，不成立。那是 Xtranslate 自己在 `languages()` 里把语对写死
     了，引擎本身支持拾语全部 8 种译文语言；今天实测两家译成日语都可用。
  2. 原票说"合规姿态：模拟网页自身请求，非伪装第三方"，不成立。Xtranslate 的必应请求带伪造
     的 Edge UA 与 Referer，它的 Google 请求反倒什么头都不带。三家都是非正式网页接口，真正的
     差别在于大陆可达性和稳定性。
  3. 谷歌从"默认关的开关"改为不做：多出一项设置，对大陆用户又根本连不上。
  4. "整段返回后模拟流式"改为分块渐进：长文实测要 9s。
  5. 引擎二级选择改为自动兜底，少一项设置。
  6. 补上原票没有覆盖的几处：语言码表、缓存放在实例之外、UA 不污染共享客户端、错误文案与
     分类器的配合、面板在场时写设置丢 Esc、批量翻译不打网页接口、发版前跑探针。
- **2026-10-04** 用户拍板 D1 = A。ADR-0013 落盘；ADR-0009、ADR-0012 的状态行已标注被修订。
  状态由 needs-triage 改为 ready-for-agent（WIP 闸门仍在）。
