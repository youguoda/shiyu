# 42 — Prompt 人设库：多风格人设 + 设置选择 + 浮窗一键切换

**What to build:** 把翻译 prompt 从单一"标准翻译引擎"扩为**人设库**：多个翻译
风格人设（标准/口语/地道俚语/正式书面/润色学术……首批五个），设置里选默认
人设，翻译浮窗里一枚 chip **即时切换**（本次翻译立即用新人设重译，选择写回
设置为本次会话默认）。参照 Xtranslate 的双 chip 交互（方向/引擎即时切换 +
写回配置）与其 prompt.js 的口语化 few-shot 人设写法。

## 人设清单（首批五个，Core 静态库 + 单测钉死）

| 人设 | 定位 | System prompt 要点 |
|---|---|---|
| 标准（默认） | 现有 TranslationPrompt 原样 | 翻译引擎：只输出译文、结构保持、已为目标语言原样返回 |
| 口语 | 聊天/发消息/回帖 | "会两门语言的朋友帮你打字"：缩写/短语动词/意译优先/语气词对应/不过度俚语化（Xtranslate EN2ZH/ZH2EN 要点） |
| 地道俚语 | 网络用语/梗/情绪强烈 | 俚语与网络用语优先译味道（"绝绝子"→"so good"），允许轻度玩梗，禁脏话升级 |
| 正式书面 | 邮件/文档/商务 | 书面词汇、完整句、礼貌层级保留、禁口语缩写 |
| 润色 | 译完再顺一遍 | 先译后按目标语习惯润色表达，不改变事实与语气强度 |

每个 System prompt 共享七条通用规则（只输出译文/不答话/专名代码链接
emoji 原样/保持长度与分段/情绪对等/错别字按意译/`<text>` 包裹防答题
——Xtranslate COMMON_RULES 的拾语版）。few-shot 示例组首批各 3~5 组，
放 Core 常量便于单测断言关键词。

## 三处接线

1. **设置**：设置·翻译加"翻译人设"选择器（五选一，写进 AppSettings
   `TranslationPersona`，默认"标准"）；ToolT каждого人设一句定位说明。
2. **浮窗 chip**：翻译面板加一枚人设 chip（现"方向 chip"同款交互）——
   点击循环或点开小菜单五选一；切换后**立即用新人设重译当前文本**；
   chip 文案显示当前人设名；选择持久化（会话内 + 写回设置）。
3. **Core**：`TranslationPersona` 枚举 + `PersonaPrompts.For(persona,
   request)`（返回 system/temperature——口语/俚语/润色可用 0.3~0.4 微高，
   标准保持 0.2）；`TranslationRequest` 加 `Persona` 字段（后端透传给
   prompt 构建）。

## 明确不做

- 自定义人设编辑器（用户自由写 system prompt）——等五预设跑通、有真实
  偏好数据后再议。
- 人设影响 Agent 动作（总结/改写）——Agent 有自己的 system prompt 体系，
  不混用。

**价值:** 同一段原文，回帖要口语、写邮件要正式、读论文要学术——人设切换
让"翻译风格"从写死变成用户手中的旋钮；浮窗一键切换是 Xtranslate 验证过的
高频高频交互（其引擎 chip 同款）。

**Blocked by:** None（纯 Core+UI，无外部依赖）。

**Status:** ready-for-agent

- [ ] TranslationPersona 枚举（五人设）+ PersonaPrompts（system/温度）+
      七条通用规则共享 + few-shot 常量，单测断言每个 prompt 含关键规则与
      目标语言插值
- [ ] TranslationRequest.Persona 字段 + 两个后端透传
- [ ] 设置"翻译人设"选择器（持久化）
- [ ] 面板人设 chip：切换即重译、文案显示当前人设、持久化
- [ ] 浮窗键位：Ctrl+P 循环人设（与 Ctrl+E 切引擎并列）
- [ ] 全量测试绿；真后端探针：口语人设下"我先撤了哈"译出口语英文；
      标准人设回归
