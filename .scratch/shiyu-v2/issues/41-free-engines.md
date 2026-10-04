# 41 — 免费引擎层：微软/谷歌/腾讯零 key 翻译（开箱即用的第一档）

**What to build:** 落实 Xtranslate 调研（docs/research/2026-09-27-xtranslate-analysis.md
§4.1）的免费引擎层——在"自备 DeepSeek key"之外增加**零配置免费引擎**，组成
三档：**免费引擎（开箱）→ 公共通道（票 36）→ 自备密钥**。设置与引导的默认
改为免费引擎，翻译功能第一次启动即可用。

- **微软 Bing 会话**（主力，Xtranslate microsoft.js 的工程细节照单全收，C# 重写）：
  GET cn.bing.com/translator 抓 params_AbusePreventionHelper（key/token/ig/iid，
  TTL 减 60s 缓存）→ POST /ttranslatev3；**并发共享 token 获取**；**401/205 强制
  刷新会话重试一次**；呼出浮窗/启动时**预热**（失败静默）。
- **谷歌 gtx**（备用，注意票 38 已把 Google 非官方端点列为 wontfix——本票引入
  需用户裁决：微软是"模拟网页自身请求"，谷歌是伪装 UA 的第三方消费，合规
  姿态不同。**默认只启用微软**，谷歌留开关默认关）。
- **腾讯**（可选第三家，视 Xtranslate tencent.js 现状存活性而定）。
- Core 新增 `BingFreeBackend : ITranslationBackend`（接口同现有），方向映射
  zh-Hans↔en；与现有 StreamingModel 管线兼容（免费引擎整段返回，按句切分
  模拟流式——同票 36 RelayBackend 手法）。
- 设置·翻译：引擎三档选择器（免费引擎/公共通道/自备密钥）；引导默认免费引擎。
- 错误处理：免费引擎失败的人话文案（"免费引擎暂时不可用，可在设置切换公共
  通道或自备密钥"）；瞬态 429/205 退避（票 34 机制复用）。

**价值:** 拾语与 Xtranslate/Glossy 差距表的最大短板"零配置"的第一步——不依赖
我们部署任何服务端（对比票 36），单客户端即可落地。

**Blocked by:** 34（退避与人话映射复用）。

**Status:** ready-for-agent

- [ ] BingFreeBackend：会话抓取/缓存/并发去重/401 刷新重试/预热 + 单测
      （parseBingAuth 用固定 HTML 夹具，不依赖网络）
- [ ] 免费引擎失败降级文案与三档选择器（设置 + 引导默认）
- [ ] 模拟流式（整段按句切分吐出）与面板/反向输入框兼容
- [ ] Google gtx 开关（默认关）+ 票 38 立场更新说明
- [ ] 全量测试绿；真后端探针：微软翻译一条（网络用例标注）
