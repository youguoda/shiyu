# 08 — 首次翻译路径：自备密钥优先

**来源：** 优化报告 O-04、O-14；判断报告 P0-3；ADR-0009
**Blocked by:** 07（已完成）
**Branch:** `v3/first-run`（已并 master，4 提交 33590c9→40b0547）
**Status:** done（0.9.0 验收轮通过后发布；验收记录见文末）

实施记录（2026-10-02）：Core（33590c9）——`ProviderPresets` 照票面核实数据落清单（百炼/DeepSeek/智谱/硅基流动进默认下拉、Kimi 入"更多"，字段含 `ExtraBody/MaxTemperature/SendTemperature/ApiKeyUrl`），`ResolveFor(settings)` 按"地址一致且模型是默认或备选"裁决，手改即视为自定义；`OpenAiCompatibleBackend` 加三个可选构造参数（附加顶层字段深拷贝合并、温度 `Min(请求值, 上限)`、可整段不发 temperature；缺省行为不变），403 `AllocationQuota.FreeTierOnly` 译成"免费额度已用完，请实名或充值"；`ConnectionProbe` 一次极小非流式请求（"hi"、max_tokens≤16、预设字段与温度规则同真翻译）三态人话：成功带耗时、密钥无效（401/403）、连不上/超时，密钥只进 Authorization 头；`RelayChannel.Available = false` 常量闸门（注释列 ADR-0009 三个上线条件），`AppSettings.IsTranslationConfigured` 据此把"选中公共通道"判为未配置、`BuildTranslationBackend` 一律落自备密钥后端；新字段 `BackendPresetId`、`RelayUnavailableNoticed`；`TranslationBatchResult` 带第一条失败人话原因。App（e331aa8 + fc4da94）——引导与设置的公共通道选项标"即将推出"且禁用（`ItemEditors.Segmented` 新增 choiceEnabled）；新 `ServicePresetRow` 供两处共用：预设下拉+申请密钥直达+测试连接三态上色，选中即代填地址/模型、手改即降级"自定义"（数据层 `ResolveFor` 再兜底）；引导加"稍后配置"（服务各项回滚基线后收尾）；设置服务页加 `service.preset` Custom 行（凭据留空沿用已存值）；面板未配置时显示"还没有配置翻译服务"引导卡（[去配置] 深链 `service.preset`、"用公共通道？暂未开放"），复制徽标/划词热键/翻译剪贴板/换方向全落此卡；启动时对选中公共通道的存量用户走 SettingsStore 一次性迁移（有自备密钥静默切 OwnKey 并托盘告知一次、没密钥保留选择仅置标记，翻译时见引导卡）；管理窗四个 Agent 动作无自备密钥时禁用并 tooltip"需要自备密钥 · 去设置"（开窗与激活时刷新），动作与批量翻译改走与面板同一后端工厂（预设规则对词典/批量同样生效），批量结束报告"成功 n（跳过 s）失败 m（首条失败原因）"。Core 753 全绿（+37 新测试，改 2 条旧钉：Relay 选中在闸门关闭时不再建 RelayBackend——此为票面要求的行为变更），App Debug 构建 0 警告 0 错误。

**What to build:**
- 公共通道在上线条件满足前不可选（引导与设置里显示"即将推出"）。已选择公共通道的老用户：如果配置了自备密钥就改用它，否则显示配置引导卡；只提示一次。
- 引导的翻译步默认自备密钥：提供国内可直连的服务商预设（依据核实过的预设清单），选中即填好地址和模型，并给出"申请密钥"链接；"测试连接"能区分密钥错误、地址错误、超时；允许"稍后配置"。
- 没配置翻译服务时，面板显示"去配置翻译服务"的引导卡，不显示异常文本。
- 管理窗的批量翻译与面板共用后端工厂；Agent 动作在没有自备密钥时禁用，并说明原因。批量翻译结束时报告"成功 n / 失败 m"。

**已核实的预设数据**（2026-10-01 调研，全部出自官方文档；完整依据在 `E:\Project\guodapro-review\provider-presets.md`）：
- 下拉顺序：阿里云百炼 `qwen-flash`（默认，唯一不加额外字段就不思考，0.15/1.5 元每百万，北京新用户 90 天 100 万 token 免费、免实名）→ DeepSeek `deepseek-flash`（**须加** `"thinking":{"type":"disabled"}`；旧名 `deepseek-chat` 已停用）→ 智谱 `glm-4-flash-250414`（永久免费、非推理，**temperature ≤ 1**）→ 硅基流动 `deepseek-ai/DeepSeek-V4-Flash`（**须加** `"enable_thinking": false`）。Kimi 放"更多"且**不发送 temperature**（固定值，传错报错）；火山方舟不进预设（model 字段未核实）。
- **因此后端要支持**（`OpenAiCompatibleBackend`）：预设可附加请求体顶层字段（关思考）；temperature 按预设限幅或不发送；百炼的 403 `AllocationQuota.FreeTierOnly` 翻成"免费额度已用完"。SSE 解析已只读 `delta.content`（`reasoning_content` 天然被忽略），补一个单测钉住。
- C# 预设清单（含 ExtraBody / MaxTemperature / SendTemperature 字段）见调研文件 §4 的 JSON。

**验收：**
- [ ] 全新隔离数据目录走完引导（全默认）后翻译一个英文单词：面板只会显示译文或配置引导卡，不出现异常文本
- [ ] 测试连接三种错误分别给出人话结果
- [ ] 没有密钥时 Agent 按钮禁用并带说明

**验收（2026-10-04，v3 收官）：** 用户在本机走完 0.9.0 验收轮——accept3 → accept18 构建、走查四至七轮；docs/manual-test-v5.md 是发版闸门（"全部通过即可发 v0.9.0 正式版"），验收中发现的问题已在发布前修复（如 B2 d2dd0ff、B4 f94f77e）——随后发布 0.9.0 / 0.9.1，用户确认关闭本票。机器可测部分：探针 28/0/1、测试全绿。关闭前的状态：ready-for-human（余引导全默认走查与测试连接三态实机；探针跳过引导，留待 Release 切换后或用户执行）
