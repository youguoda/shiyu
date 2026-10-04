# 41 — 免费引擎层：零 key 翻译（微软默认 + 腾讯备用，开箱即用）

**What to build:** 落实 Xtranslate 调研（docs/research/2026-09-27-xtranslate-analysis.md
§4.1/§6）的免费网页引擎层——在"自备 DeepSeek key"之外增加**零 key 零注册**
的免费引擎，组成三档：**免费引擎（开箱）→ 公共通道（票 36）→ 自备密钥**。
设置与引导的默认改为免费引擎，翻译功能第一次启动即可用。

## 引擎清单（源码核对完毕，含免费资源全景）

### 默认启用

- **微软 Bing（主力）**：`cn.bing.com/translator` 抓会话（params_AbusePreventionHelper
  的 key/token/ig/iid，TTL 减 60s 缓存）→ POST `/ttranslatev3`。工程细节照单全收：
  **并发共享 token 获取**（pending 去重）、15 秒提前刷新、**401/205 强刷会话重试
  一次**、呼出浮窗/启动时**预热**（失败静默）。C# 重写，parseBingAuth 用固定 HTML
  夹具单测（不依赖网络）。**合规姿态：模拟网页自身请求**，非伪装第三方。

### 默认启用（备用）

- **腾讯 TranSmart**：`transmart.qq.com/api/imt`，国内 ~0.2s 最快（口语质量一般）；
  `text_list` 按行数组提交（服务端逐行译，返回 join）。作为微软失败时的手动/自动
  备用。

### 默认关闭（需用户裁决）

- **谷歌 gtx**：`translate_a/single?client=gtx` 一行 GET。**票 38 wontfix 已把
  Google 非官方端点列为不做**（灰色用法，伪装风险）；Xtranslate 同样在用。
  实现为可选开关**默认关**，设置里标注合规提示，开关状态写进配置。

### 免费大模型（不进本票）

智谱 GLM-4-flash（国内直连长期免费）、硅基流动、Gemini、Groq、Cerebras、
OpenRouter `:free` 组、通义送量、Ollama 本地——属 **票 36 公共通道 / 自备密钥**
档；OpenRouter 的"免费模型发现"（`:free` 过滤 + 不适合翻译正则 + context 排序）
已记录在调研报告 §6，届时复用。

## 工程规格（照搬 Xtranslate 验证过的参数）

- 会话缓存：`exp = now + max(ttl - 60s, 0)`；15 秒内复用缓存；并发去重
  （pending Promise 共享）。
- 请求：POST form（fromLang/text/to/token/key）+ UA + Referer；响应 205 →
  token 失效强刷一次。
- 失败人话文案：接票 34 的错误码映射（"微软翻译拒绝了这次请求，换一个引擎或
  改用大模型试试"），引擎间手动切换入口留给设置页。
- **模拟流式**：免费引擎整段返回，按句切分后以 onPartial 节奏吐出（同票 36
  RelayBackend 手法），保持面板/反向输入框的流式观感。
- 语向：zh-Hans↔en（两家均只支持中英对，与票 40 反向输入框场景一致）。

## 设置与引导

- 设置·翻译：后端三档选择器首位 = "免费引擎（无需配置）"；免费档内
  微软/腾讯二级选择；谷歌开关（默认关 + 合规提示）。
- 引导：默认免费引擎，翻译功能首启即可用；公共通道/自备 key 降为可选。
- 失败降级链文案："免费引擎暂时不可用，可在设置切换公共通道或自备密钥"。

**价值:** 零 key 零注册开箱即用——不依赖我们部署任何服务端（对比票 36），
单客户端即落地；堵住"没填 key 翻译等于不存在"的最大短板。

**Blocked by:** 34（退避与人话映射复用）。

**Status:** ready-for-agent

- [ ] BingFreeBackend（C#）：会话抓取/缓存/并发去重/401 刷新重试/预热 +
      parseBingAuth 夹具单测
- [ ] TencentFreeBackend：text_list 按行提交/拼接 + 单测
- [ ] 模拟流式（整段按句切分吐出）与面板/反向输入框兼容
- [ ] 免费引擎失败降级文案（接票 34 错误码映射）
- [ ] 三档选择器（设置 + 引导默认免费引擎）+ 谷歌开关默认关 + 合规提示
- [ ] 全量测试绿；真后端探针：微软翻译一条（网络用例标注）、断网降级探针
