# 38 — wontfix 存根：明确不做项 + 发布守则落 docs

**What to record:** Glossy 调研（docs/research/2026-09-27-glossy-analysis.md
§9.5）的"明确不做"与一条发布守则，立此存照——未来重议时引用本票，不丢上下文。

## 明确不做（含理由）

- **Google 非官方端点 + 伪装 UA**：灰色用法，封禁与合规风险；Glossy 自己都要
  靠双 client + 120s 冷却硬扛——不做。
- **单位货币换算**：与拾语"效率工具"定位弱相关（Glossy 是阅读工具所以合理）
  ——不做；用户反馈需要时重议。
- **剪贴板轮询取词**：Glossy 非 Windows 才轮询；拾语已事件驱动——不做。
- **UIA 上下文句**（单词卡"所在原句"）：有价值但 UIA TextPattern 的应用
  兼容性是泥潭——backlog，待票 35 单词卡跑通后再评估。

### 补充（2026-10-04，票 41）：Google gtx 的实测证据

票 41 引入必应/腾讯网页接口时（ADR-0013 接受了它们的同类风险），重新评估了 Google 非官方
端点（`translate.googleapis.com` 的 gtx 客户端），**维持不做**，理由换成实测：

- **直连（`--noproxy '*'`，即没开代理的大陆用户）：8 秒超时**——大陆不可达，没有可用的免费引擎
  价值可言。
- **走系统代理（127.0.0.1:7890）：第一个请求就是 429**。
- 作为"默认关的开关"也不值：多出一项设置（CONTEXT.md：设置项越少越可信），而对多数用户它根本
  连不上。

必应与腾讯的对照数据（同一天直连实测）与探针见票 41、`tools/probes/probe-free-engines.ps1`。

## 采纳的发布守则（本票落地）

Glossy 最大红旗：v1.5.x release notes 描述了 OCR/文档翻译/回填，但对应 tag
源码里不存在这些功能（公开仓库滞后于私有开发）。教训直接适用于拾语票 28 的
GitHub Releases 更新通道。

**守则：release notes 描述的每个功能，必须能在对应 tag 的源码里指出来源。**

- [x] docs/release-checklist.md 落此守则并入发布流程
- [x] 本票作为"明确不做"的存根

## DAG 处置记录（2026-09-27）

本票是**登记册**（存根+守则记录），无实现做功项——两项勾选在 DAG 启动前已
由 577ea5d 完成（release-checklist 落盘）。DAG 执行中作为约束面被遵守：票
34-37/39/40 未引入任何"明确不做"项（Google 非官方端点/轮询取词/换算/UIA
上下文句均未出现）；票 36 的公共通道未采用 Glossy 端点伪造。协调者决定：按
登记册结案（ready-for-human），不做基线满足仪式——存根即产物。

**Status:** done (succession via v3 - see docs/manual-test-v5.md)

**Acceptance succession (2026-10-03, ticket 28):** closed via v3 - machine-testable parts are covered by the probe suite (28 checks, 0 fail) and 938+3 automated tests; human-judgement items live in docs/manual-test-v5.md.
