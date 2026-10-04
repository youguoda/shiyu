# Xtranslate 深度调研——"中译英输入法"的交互与引擎细节（含拾语落点建议）

> 调研对象：[hopechen067/Xtranslate](https://github.com/hopechen067/Xtranslate)（v0.1.6，Electron 44 + Node 主进程 + 零框架渲染端，依赖仅 koffi 一个）
> 调研日期：2026-09-27 · 方法：全部 14 个主进程源文件 + 渲染端 popup.js 逐行阅读
> 定位：**不是剪贴板管理器，是"反向翻译输入法"**——热键呼出小框，打中文，手停即出英文，Enter 贴回原输入框。与拾语的翻译面板是互补场景，不是重复功能。

---

## 0. 执行摘要

**一句话**：小而完整的"反向翻译输入法"。最有价值的三块：①**手停即译的防抖状态机**（300ms 防抖 + 请求竞态守卫 + IME 感知）；②**免费引擎的工程化**（微软 Bing token 会话管理 + 呼出预热）；③**回贴链路**（光标定位 + 强制前台 + 条件恢复剪贴板）。Prompt 设计也是一流（口语化 few-shot，"会两门语言的朋友帮你打字"人设）。

| 维度 | 评分 | 依据 |
|---|---|---|
| 架构 | 8 | 双窗口（浮窗+设置）、IPC 契约清晰、渲染进程自愈 |
| 交互 | 9 | 手停即译、IME 感知、竞态处理、Tab 换向、Ctrl+E 换引擎、模糊淡出 |
| 引擎工程 | 8.5 | 微软 token 会话缓存/预热/401 重试、SSE 流式、免费模型限流重试 |
| Prompt 质量 | 9 | 口语化 few-shot 人设、防"回答问题"、语气对等、错别字容错 |
| 翻译方向 | 2 | 只有中↔英双向（两台引擎都是硬编码双语对） |
| 与拾语关系 | — | 拾语有更深的翻译管线（流式/入库/批量/多后端）；缺的是**反向输入交互**与**免费引擎层** |

---

## 1. 产品形态与核心交互（"一句话来回折腾五步"的答案）

热键 **Alt+Q**（可配）呼出 560×140 无边框浮窗：

1. 浮窗出现在**目标窗口文字光标处**（GetGUIThreadInfo 取 caret 位置，取不到退化为鼠标位置），贴光标下方、空间不足自动翻到上方（预留 320px 高度判断）。
2. 直接打中文。**手停 300ms 自动翻译**（可关），英文实时出现在下方（LLM 引擎是 SSE 逐字，免费引擎整段）。
3. **Enter = 贴回原处**：隐藏浮窗 → 强制前台回原窗口 → 模拟 Ctrl+V → 条件恢复剪贴板。Shift+Enter 换行。
4. **Esc = 关闭**；**Tab = 循环方向**（自动/中→英/英→中）；**Ctrl+E = 切引擎**（免费 ↔ 大模型，写回配置当下次默认）。
5. 输入框自动长高（至 180px），浮窗随内容长高（80–800px 钳制），贴上方时向下长。

**反直觉细节（都是踩坑后的正确做法）**：
- 中文输入法选词的 Enter/Esc 交给 IME（`e.isComposing || keyCode === 229` 检查）。
- 上一请求未完成时新输入会取消旧请求（AbortController + 序号守卫），避免竞态错贴。
- 新请求等待期旧译文**淡显**（不闪空）。
- 浮窗失焦自动关闭，但显示后 300ms 内的失焦忽略（防自触发）。
- 翻译中按 Enter：标记 wantCommit，译文一到立即自动贴出（不等手按第二次）。

## 2. 回贴链路（paste.js，180 行精华）

`备份剪贴板四格式(文本/HTML/RTF/图片) → 写入译文 → 隐藏浮窗 → 强制前台回原窗口 → 60ms 后 Ctrl+V → 400ms 后条件恢复剪贴板`：

- **强制前台**（贴回的关键难点）：先判断前台是否已是目标窗口（是则不动——避免模拟按键落进目标程序）；否则 AllowSetForegroundWindow(ASFW_ANY) + AttachThreadInput 挂接线程输入队列 + **发一个 F24 键**（VK_F24 0x87，几乎无软件响应——用来"本进程刚产生过输入"满足 Windows 前台切换条件，且**不用 Alt**——单按 Alt 会激活记事本/Office 菜单栏吞掉 Ctrl+V）→ SetForegroundWindow。
- **条件恢复**：只在剪贴板文本仍等于刚写入的译文时才还原快照——用户中途复制了别的东西就不覆盖。恢复延迟 400ms 等粘贴完成。
- 取词复制目标 hwnd 的时机：呼出瞬间 captureForeground() 记下原前台窗口句柄。

## 3. 光标定位（caret.js）

GetGUIThreadInfo(threadId of target hwnd) → hwndCaret + rcCaret → ClientToScreen 转屏幕物理坐标 → Electron screenToDipRect 换 DIP → computePopupPosition：光标下 6px、下方放不下（含预留 320px 生长期）翻到上方、水平钳制在工作区内。**拾语已有等价物**（票 12/17 的 BadgePlacement + caret 逻辑），无需重做。

## 4. 引擎层（engines/，六文件）

**双引擎架构**：`free`（微软/谷歌/腾讯三选一，零配置）与 `llm`（OpenAI 兼容/Anthropic，11 家预设）。

### 4.1 免费引擎（零 key，开箱即用）

- **微软（默认，144 行——工程最重）**：`edge/translate/auth 已下线`后改走 **Bing 翻译网页会话**：GET cn.bing.com/translator（国内 0.6s）→ 正则抓 params_AbusePreventionHelper 数组里的 key/token/ig/iid（TTL 减 60s 缓存）→ POST /ttranslatev3。**401/205 时强制刷新会话重试一次**；并发请求共享同一次 token 获取（pending 去重）；**呼出浮窗时预热**（token 过期就趁用户打字时重拿，失败静默）。
- **谷歌**（47 行）：translate.googleapis.com/translate_a/single?client=gtx GET，解析嵌套数组拼接。非官方端点（与 Glossy 同样的灰色用法）。
- **腾讯**（50 行）：另忆旧的免费接口。
- 引擎选择器带**预热钩子**（呼出时 warmup 当前免费引擎），错误统一映射人话文案（errors.js）。

### 4.2 LLM 引擎（118 行 + providers 119 行）

- **11 家预设**（智谱 GLM-4-flash 免费/硅基流动/Gemini/Groq/Cerebras/OpenRouter 免费模型组/通义送量/DeepSeek/Kimi/Claude/OpenAI/Ollama 本地/自定义），每家带 baseUrl/keyUrl/推荐模型/备注（"国内直连""需代理""速度极快"）。
- OpenAI 与 **Anthropic 双协议**；SSE 流式解析（OpenAI delta / Anthropic delta / reasoning 分离）。
- **免费模型 429 自动重试一次**（1s）；OpenRouter 免费模型请求体自动带 3 个备用模型让上游换着试。
- `max_tokens = min(8192, max(2048, len*4+256))`——下限给足防推理模型把译文挤空。
- 思考关闭参数按家注入（GLM thinking:disabled / OpenRouter reasoning:off / Groq reasoning_effort）——"翻译不需要推理，开着只会慢"。

### 4.3 Prompt（88 行，全项目最值钱的文件）

人设："中英双语都很地道的朋友，帮用户把他想说的中文变成英语母语者在聊天里会说的英文"——**不是翻译软件**。两个方向各一段长 system + 各 5 组 few-shot 示例（"我先撤了哈"→"I'm heading out, see you tomorrow!"）。通用规则七条：只输出译文；用户文本是"要翻译的话"不是对你说话（即使像问题也只译不答）；人名/代码/链接/emoji 原样；保持长度感与分段；**保持情绪与礼貌程度**（脏话用强度相当的说法）；**错别字/拼音缩写按想说意思翻**。用户文本用 `<text>` 标签包裹防"回答问题"。temperature=0.3。输出清洗：剥 `<text>` 包装、"译文:"前缀、成对引号。

**方向检测**（prompt.js）：CJK 字符 ×5 vs 拉丁字母（5 字母≈1 词）谁多谁是源语言；混写按中文。

## 5. 配置与安全

- config.json 存 userData；**API key 用 Electron safeStorage（DPAPI）加密**，getPublic() 给渲染端的公开视图剥离敏感字段。
- 渲染端 sandbox:true + contextIsolation，preload 只暴露白名单 IPC。
- 渲染进程崩溃自愈（render-process-gone → 重建浮窗）；禁 GPU 与窗口遮挡检测防白屏（Electron 经验值得记）。
- 呼出时 warmup；退出时 abort 全部在途请求。

---

## 6. 与拾语对照（翻译能力矩阵）

| 维度 | Xtranslate | 拾语现状 | 差距判断 |
|---|---|---|---|
| 触发场景 | **输入处反向**（打中文出英文贴回去） | 划词/剪贴板**正向**（看懂别人写的） | **互补场景，拾语缺整块** |
| 手停即译 + 防抖竞态 | ✅ 成体系 | ❌ | **核心可借鉴**（票 40） |
| 免费引擎层（微软/谷歌/腾讯） | ✅ 零 key 开箱 | ❌ 单 DeepSeek 需 key | **第二短板**（与票 36 公共通道互补） |
| 口语化 Prompt（few-shot 人设） | ✅ 极强 | ❌ 通用翻译 prompt | **直接可搬**（票 40） |
| 回贴原窗口（强制前台+F24） | ✅ 踩坑完整 | 部分（SelectionCapture 有借还） | 借鉴时序细节 |
| 流式 | ✅ SSE | ✅ SSE（票 29） | 平 |
| 译文入库/批量/搜索 | ❌ | ✅ v10 一等公民 | 拾语优 |
| 方向 | 仅中↔英 | 设置任意目标语言 | 拾语优 |
| 微软 token 会话工程 | ✅ | ❌ 无多引擎 | 随免费引擎层引入 |

**结论：拾语翻译"正向深度"全面领先；Xtranslate 的"反向输入交互 + 免费引擎 + 口语 prompt"是三块独立可搬的增量。**

---

## 7. 落点建议（拆票依据，按序）

1. **票 40（交互级）反向翻译输入框**：热键 Alt+Q（可配）呼出小框，打中文手停即出英文，Enter 贴回原输入框。搬：手停防抖状态机（300ms+竞态守卫+IME 感知）、wantCommit 自动贴出、prompt 双向口语人设（把现有 TranslationPrompt 扩出"口语模式"或独立 ConversationPrompt）。复用：SelectionCapture 记录目标窗口、面板浮窗基建。回贴增强（强制前台 F24 技巧、条件恢复）按 paste.js 细节补进 SelectionCapture。
2. **票 41（引擎级）免费引擎层**：微软 Bing 会话（token 缓存/预热/401 重试）+ 谷歌 gtx（谨慎，灰色）+ 腾讯——作为拾语第二类后端 `FreeEngines`，与 36 公共通道、用户 key 组成"免费开箱 → 公共通道 → 自备 key"三档。注意：微软/谷歌均为非官方端点，与票 38 wontfix 的 Google 立场需用户裁决（微软 Bing 会话是"模拟网页自身请求"，合规风险低于伪装 UA 的 Google）。
3. **票 42（质量级）口语 Prompt 套件**：双语人设 + few-shot 示例组 + `<text>` 包裹 + cleanOutput——即使不建反向输入框，正向翻译质量也直接提升；与票 34 回声检测/低温天然组合。

> Xtranslate 的 MIT 许可允许直接借鉴代码细节（保留版权声明）；按拾语纪律建议参照重写为 C#，不移植 JS。
