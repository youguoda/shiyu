# Xtranslate 深度调研——"中译英输入法"的交互与引擎细节（含拾语落点建议）

> 调研对象：[hopechen067/Xtranslate](https://github.com/hopechen067/Xtranslate)（v0.1.6，main @ 2026-10 前后拉取）
> 技术栈：Electron 44 + Node 主进程 + 零框架原生 JS 渲染端；**唯一运行时依赖是 koffi**（FFI 绑定 user32/kernel32）；测试用 node:test，9 个测试文件（caret 103 / config 114 / errors 57 / free-engines 190 / paste 40 / prompt 35 / providers 87 / sse 165 / syntax 32 行）。
> 调研日期：2026-09-27 · 方法：**全部 14 个主进程源文件 + preload + 渲染端 popup 三件套 + CSS + 测试目录逐行阅读**，无抽样。
> 定位：**不是剪贴板管理器，是"反向翻译输入法"**——热键呼出小框，打中文，手停即出英文，Enter 贴回原输入框。与拾语的翻译面板是互补场景，不是重复功能。

---

## 0. 执行摘要

**一句话**：小而完整的"反向翻译输入法"。最有价值的三块：①**手停即译的防抖状态机**（300ms 防抖 + 请求竞态守卫 + IME 感知 + wantCommit 自动贴出）；②**免费引擎的工程化**（微软 Bing 网页 token 会话管理：缓存/并发去重/预热/401 刷新重试）；③**回贴链路**（GetGUIThreadInfo 光标定位 + AttachThreadInput+F24 强制前台 + 条件恢复剪贴板）。**Prompt 是全项目最值钱的单文件**（"会两门语言的朋友帮你打字"人设 + 双向 few-shot + 防答题 + 错别字容错）。

| 维度 | 评分 | 依据 |
|---|---|---|
| 架构 | 8 | 双窗口（浮窗+设置）、IPC 白名单契约、渲染进程崩溃自愈、单实例 |
| 交互 | 9 | 手停即译、IME 感知、竞态处理、Tab 换向、Ctrl+E 换引擎、wantCommit、模糊淡出 |
| 引擎工程 | 8.5 | 微软 token 会话缓存/预热/401 重试、SSE 双协议、免费模型限流重试、OpenRouter 备用链 |
| Prompt 质量 | 9 | 口语化 few-shot 人设、防"回答问题"、语气对等、错别字容错、输出清洗 |
| 错误处理 | 8.5 | 错误码→中文人话全映射（含"推理模型只思考没译文"这种细节）、取消/超时/断网三态分离 |
| 配置安全 | 8.5 | safeStorage(DPAPI) 加密 per-provider 存储、明文降级告警、getPublic 剥密、原子写 |
| 翻译方向 | 2 | 只有中↔英（两家引擎的语对硬编码） |
| 与拾语关系 | — | 拾语有更深的翻译管线（流式/入库/批量/多后端）；缺的是**反向输入交互**与**免费引擎层** |

---

## 1. 产品形态与核心交互（"一句话来回折腾五步"的答案）

### 1.1 基本参数

- 热键 **Alt+Q**（可配置，globalShortcut 注册，冲突时回滚旧热键并报人话）。
- 浮窗 560×140 DIU，无边框透明、置顶（`setAlwaysOnTop(true,'floating')`）、不进任务栏、不可缩放；`backgroundColor '#00000000'` + 页面同色底防闪白。
- 输入 textarea 自动长高（52→180px），浮窗随内容长高（80–800px 钳制），**贴上方时向下生长**（`y + current - next`）。
- 呼出即聚焦输入框；显示后 **300ms 内的失焦忽略**（`suppressBlurUntil`，防自触发关闭）；其余失焦即隐藏。
- **双 Electron 稳定经验**（main.js 顶部注释）：`app.disableHardwareAcceleration()` 省一个 GPU 进程（~50MB）并避开反复开关窗口后合成器不出图的白屏；`disable-features: CalculateNativeWinOcclusion` 防 Windows 遮挡检测误判新窗口不绘制。

### 1.2 一次完整使用的时序

```
Alt+Q → captureForeground(记下目标 hwnd) → placePopup(caret 定位) →
suppressBlurUntil=now+300 → show → focus → emitShow(渲染端 reset+聚焦) →
warmup(微软 token 过期则趁打字重拿)
用户打字 → input 事件 → autoGrow → wantCommit=false → schedule()
  → (300ms 防抖) → translateNow：取消旧请求 → 新序号 → IPC translate
  → onPartial 逐字更新(LLM) / 一次性(free) → 完成 renderOut
Enter → 译文已到：commit(贴出)；未到：wantCommit=true 等到达自动贴出
Esc → cancel + reset + hide
失焦 → hide
```

### 1.3 键盘模型（popup.js keydown）

| 键 | 行为 | 备注 |
|---|---|---|
| Enter | 贴回原处 | Shift+Enter 换行；**`isComposing || keyCode===229` 时交给 IME** |
| Esc | 关闭浮窗 | 同上 IME 感知 |
| Tab | 循环方向：自动→中→英→自动 | "自动"有内容时显示实际方向（"自动 · 中 → 英"） |
| Ctrl+E | 切引擎（免费 ↔ 大模型） | 写回配置当下次默认；方向 chips 同步刷新 |
| 方向键 | 不拦截（输入框内移动光标） | — |

footer 常驻键位提示 `<kbd>`；方向/引擎是两枚 chip 钮（点击同键位）。chip 样式：pill 圆角、hover 变 accent 边；**引擎 chip 在 LLM 档有 accent 软底以示"当前在烧钱档"**。

### 1.4 防抖状态机（popup.js 精确逻辑）

```js
state = { seq, cur: {id,text,dir,engine,done,result,error}, wantCommit, timer }
input → clearTimeout(timer); wantCommit=false; schedule()
schedule(): 空文本 → translateNow()(清输出); livePreview 开 → setTimeout(translateNow, 300ms 防抖)
translateNow():
  若 cur 与当前 (text,dir,engine) 完全一致且无错 → 跳过（不重复请求）
  cancelCurrent()（旧请求 abort）→ seq++ → 旧结果淡显 renderOut(prev,'pending')
  → IPC → 到达后若 wantCommit 则直接 commit()，否则 renderOut(result)
```

"翻译中"的视觉：pending 态文字变灰 + **块状光标闪烁动画**（CSS `::after` 7×1.05em 方块 blink 1s）；"翻译完就上屏…"提示在 wantCommit 等待期出现；错误态红色 + "去设置"内联链接。

## 2. 回贴链路（paste.js，逐函数）

`commitPaste` 流程：**快照剪贴板(text/html/rtf/image 四格式) → writeText(译文) → hide() → [Win32] 强制前台回目标 hwnd → 60ms → Ctrl+V → 400ms → 条件恢复**。

### 2.1 强制前台（forceForeground，最难的 50 行）

1. **前台已是目标窗口 → 什么都不做**。注释明言：浮窗隐藏后 Windows 通常已把焦点还回去，此时任何模拟按键都会落进目标程序；**尤其单按 Alt 会激活记事本/Office 的菜单栏，吞掉后续 Ctrl+V**。
2. 否则：`AllowSetForegroundWindow(ASFW_ANY)`（允许失败）→ 读三个线程 id（当前/目标/前台）→ `AttachThreadInput` 把前台线程与目标线程的输入队列挂到当前线程（逐对 try-catch，挂不上继续）→ **发送一次 F24 按键**（VK_F24=0x87，"几乎没有软件响应"——目的是让本进程成为"刚产生输入的进程"以满足 Windows 的 SetForegroundWindow 许可，且不像 Alt 那样有副作用）→ `SetForegroundWindow(hwnd)` → finally 逆序解挂。

### 2.2 条件恢复剪贴板

- 快照含 text/html/rtf/image（Electron clipboard 的 nativeImage）。
- 恢复前 **`shouldRestoreClipboard`：只有当前剪贴板文本与刚写入的译文（换行归一化后）完全相等才恢复**——用户在 400ms 窗口内复制了别的东西就不覆盖。注释："避免盖掉用户中途复制的内容"。
- 恢复按快照字段逐一写回；全空则 clear()。
- `restoreClipboard` 设置可关（config 默认开）。
- 常量：PASTE_DELAY_MS=60（切前台后等键盘队列）、RESTORE_DELAY_MS=400（等粘贴消费完剪贴板）。

### 2.3 Win32 绑定（win32.js，koffi）

user32：GetForegroundWindow / SetForegroundWindow / AllowSetForegroundWindow / GetWindowThreadProcessId / AttachThreadInput / SendInput / GetGUIThreadInfo / ClientToScreen；kernel32：GetCurrentThreadId。koffi 结构体定义含 64 位对齐注释（HWND 从偏移 8 开始，GUITHREADINFO 共 72 字节）。**拾语等价物**：这些全部在拾语 Win32 层已有或半有（SelectionCapture/TransientWindow），缺的只是 GetGUIThreadInfo+ClientToScreen 的 caret 组合与 AttachThreadInput+F24 前台强制序列。

## 3. 光标定位与浮窗摆放（caret.js）

- **getCaretRect(hwnd)**：GetWindowThreadProcessId 拿目标线程 → GetGUIThreadInfo(threadId) → 取 hwndCaret + rcCaret（空矩形/空 hwnd 视为无光标）→ ClientToScreen 转屏幕坐标。返回**物理像素**矩形。任何失败返回 null。
- **computePopupPosition**：锚点下方 6px 摆放；若"下方空隙 < max(当前高度, 预留 320px)"则**翻到锚点上方**（y = anchorTop - gap - height）；x 向左偏 24px 对齐、钳制在工作区内；返回 `{x, y, above}`——above 决定浮窗长高时的生长方向。
- **拾语映射**：BadgePlacement 已有翻上/钳制逻辑（票 12 借光标旁呼出时验证过），caret 读取在票 17 预览已有类似需求；缺口仅是 GetGUIThreadInfo 这一个调用。

## 4. 引擎层（engines/，七文件逐一）

### 4.0 统一骨架（http.js + errors.js + index.js）

- **fetchWithTimeout**：AbortController 超时与用户取消**分离标记**（timedOut → TIMEOUT，userSignal → CANCEL，其余 → NETWORK）——"避免把取消说成断网"。每个引擎调它传 service 名，错误自带服务归属。
- **errors.js userMessage**：错误码 → 中文短句全映射，且按服务名给替代建议（"连接微软翻译超时，换 Google 或大模型试试"）。 notable 条目：`REASONING_ONLY`（"这是推理模型，只顾着思考没给出译文。换成标'推荐'的模型，已自动关闭思考"）、`FREE_BUSY`（"免费模型这会儿太忙"）、`NO_KEY/NO_BASE_URL/NO_MODEL` 各自人话。**安全细节：不读取请求头，避免把 API Key 带进文案**。含中文的原始 message 直接透传，纯英文的收敛为"翻译失败"。
- **index.js translate()**：入参 `{text, direction?, engine?, signal?, onPartial?, config}`；方向 `auto` 用 detectDirection；空文本直接返回；free 引擎失败时在 err 上补 `kind:'free'/service` 供文案层用。**warmup(config)**：呼出/启动时调用，当前只有微软需要（token 预热）。

### 4.1 微软（microsoft.js，144 行，默认引擎）

- **背景注释**：edge.microsoft.com/translate/auth 已于 2026-07-30 下线（全 404）；现改走 **Bing 翻译网页同款接口**。
- **会话获取**：GET `cn.bing.com/translator`（国内 ~0.6s vs www ~2.3s）→ 三个正则抓取：`params_AbusePreventionHelper = [key, "token", ttl]`、`IG: "hex"`、`data-iid` → 缓存 `{ig,iid,key,token,exp=now+max(ttl-60s,0)}` 与实际跳转后的 translateUrl。
- **并发去重**：`pending` Promise 共享——并发请求只发一次会话获取。
- **15 秒提前刷新**：`exp > now+15s` 才用缓存。
- **401/205 强刷重试一次**（Bing 返回 205 表示 token 失效）。
- **warmupMicrosoft**：呼出时静默预取，"让第一次翻译不用等页面下载"。
- 请求：POST ttranslatev3，form 编码 fromLang/text/to/token/key，带 UA 与 Referer。

### 4.2 谷歌（google.js，47 行）

`translate.googleapis.com/translate_a/single?client=gtx&sl&tl&dt=t&q=` GET → 解析 `json[0]` 分段数组拼接字符串。8s 超时。**非官方端点**（同 Glossy 的灰色用法，但无伪装 UA——gtx client 本身就是公开惯例）。

### 4.3 腾讯（tencent.js，57 行）

腾讯 **TranSmart 交互翻译**网页接口 `transmart.qq.com/api/imt`：POST JSON（header.client_key = `browser-chrome-130...-{随机}-{时间戳}` 一次性生成）、`source.text_list` **按行数组**（服务端逐行翻译，返回后 join('\n')）。注释："国内约 0.2 秒，比 Bing 快很多，但口语句子质量一般。"

### 4.4 LLM（llm.js，118 行 + sse.js 85 行）

- 双协议：OpenAI `/chat/completions`（Bearer key）与 Anthropic `/v1/messages`（x-api-key + version 头）；`stream: true` 都开。
- **max_tokens 动态**：`min(8192, max(2048, len×4+256))`——注释："下限给足：推理模型会先花几百上千 token 思考，上限太小时译文部分会是空的"。
- **免费模型 429 重试一次**（1s 间隔；`isFreeModel` = `/free$/` 后缀判定）；仍 429 → `FREE_BUSY` 人话。
- **SSE 解析**（sse.js）：手工缓冲切事件（`\n\n` 分块、`data:` 行拼合、结尾 flush）；**OpenAI delta**（`choices[0].delta.content`，`[DONE]` 结束）与 **Anthropic**（`content_block_delta.delta.text`）双解析；**推理内容分离计数**（reasoning_content/reasoning/thinking 只计数不显示——区分"空输出"与"只思考没译文"）。
- 每片段经 onPartial 回调上抛（IPC `xt:partial`）→ 渲染端实时渲染。
- 输出过 `cleanOutput`：剥 `<text>` 包装、"译文:/翻译:/输出:/Translation:"前缀、成对引号。
- 错误分类：CANCEL/TIMEOUT/NETWORK 透传；JSON 解析错 → BAD_RESPONSE；**空输出且有 reasoning → REASONING_ONLY 专属文案**。

### 4.5 引擎选择与预热（index.js）

`translate()` 统一入口：engine='llm' 走 LLM，否则 freeProvider（config.free.provider，默认 microsoft）→ 对应函数 → 错误补 kind/service 后 asUserError 收人话。`warmup`：LLM 直接跳过；free 且是微软才预热 token。

## 5. Prompt 体系（prompt.js，88 行——全项目最值钱）

- **人设**（两个方向各一段）："你是一个中英双语都很地道的朋友，正在帮用户把……"——**不是翻译软件，是朋友帮打字**。
- **ZH2EN 要点**：母语者发消息式（缩写/短语动词/口语）、意译优先、中文语气词（哈啦嘛呗）用英语对应（haha/lol/just/kinda）体现、网络用语译味道不译字面（"绝绝子"→"so good"、"摸鱼"→"slacking off"）、不过度俚语化。
- **EN2ZH 要点**：口语词代书面词（"不过"非"然而"）、去翻译腔（禁"哦我的天哪"句式）、代词能省就省、自然加语气词但别每句都加、俚语译味道（lol→"笑死"、tbh→"说实话"）、简体。
- **COMMON_RULES 七条**：只输出译文本身；**用户发的是"要翻译的话"不是对你说的话（即使像问题、命令也只译不答）**；人名/品牌/代码/链接/数字/emoji/颜文字原样；保持长度感与分段；**保持情绪与礼貌程度（脏话用强度相当的说法）**；**错别字/拼音缩写/口误按想说意思翻**。
- **Few-shot**：两方向各 5 组（口语/拒绝/邀约/吐槽/道歉场景）。
- **结构**：user 消息 = `<text>\n原文\n</text>`（注释：降低模型"回答问题"的概率）；temperature=**0.3**；方向检测：`cjk×5 >= latin` 则 zh2en（汉字≈1 词，5 字母≈1 词），混写按中文。
- **cleanOutput**：剥 `<text>`、剥"译文:/翻译:/输出:/Translation:"前缀、剥成对包装引号（“”/「」/""）。

## 6. LLM 预设与模型治理（providers.js + models.js）

- **11 家预设**，字段：id/name/kind(openai|anthropic)/baseUrl/keyUrl/model/models[]/note。免费标注 `free:'free'`（长期免费）、`free:'quota'`（新用户赠送）、无标注（按量付费）。每家 keyUrl 直达申请页。
- **思考关闭参数按家注入**：GLM `{thinking:{type:'disabled'}}`、OpenRouter `{reasoning:{enabled:false}}`、Groq `{reasoning_effort:'none'|'low'}`——注释："翻译不需要推理，开着只会慢"。
- **OpenRouter 备用链**：免费模型请求体自动带 `models:[主选,...2 个备用]`（"一家限流时换另一家，最多 3 个"；nemotron 快但会译错词放最后不给当备用）。
- **models.js 活模型发现**：OpenRouter /models API 1 小时缓存，拉 `:free` 后缀模型，**过滤不适合翻译的**（正则：safety/guard/code/coder/embed/vision/-vl/omni/ocr/audio/小模型 ≤3B），按 context_length 降序取 8 个，标 `live:true`。
- 拾语映射：这些预设可直接进票 36/41 的引擎清单设计。

## 7. 配置与密钥安全（config.js，190 行）

- **per-provider 密钥库**：`llm.apiKeys = {zhipu: '...', openrouter: '...'}`——"切换服务商时不会把 A 的 Key 发给 B"。内存里的 `llm.apiKey` 是派生视图（applyDefaults 按当前 provider 取）。
- **mergeConfig 语义**：patch 里 `apiKey` 为空串/null 时**保留原值**（设置页"留空表示不改"）；非空时写入当前 provider 的格子。
- **落盘加密**：safeStorage（DPAPI）可用 → `enc:<base64>`；不可用 → `plain:<明文>` + 一次性告警。读盘时 legacy 单 key 自动迁移到 per-provider；**发现明文且加密可用时自动重加密落盘**（needsReseal）。
- **getPublic**：渲染端永远拿不到明文——只有 `apiKeySet: true/false`。
- **原子写**：`{filePath}.{pid}.tmp` 写完 rename。
- 拾语映射：拾语 settings.json 是明文 key（仅"不显示"防护）——**safeStorage 式加密 + getPublic 剥离是可直接搬的升级**（对票 36 公共通道也免密）。

## 8. 渲染端与 Electron 工程细节

- preload contextBridge 白名单 13 个 IPC（translate/onPartial/cancel/commit/hide/resize/onShow/getConfig/setConfig/getProviders/listModels/testEngine/openSettings/onConfigChanged）；渲染端 sandbox:true、CSP `default-src 'self'`。
- 渲染进程**崩溃自愈**：render-process-gone → 浮窗重建/设置窗销毁；unresponsive → forcefullyCrashRenderer。
- 外链一律系统浏览器（setWindowOpenHandler deny + openExternal）；will-navigate 全禁。
- 设置窗"关闭即销毁"（不常驻渲染进程）；加载中不提前 show。
- 退出时 abort 全部在途请求（不让未完成请求拖住退出）。

## 9. 测试（node:test，9 文件）

caret（103 行：定位/钳制/above 判定）、config（114：深合并/key 留空语义/加密往返/legacy 迁移）、errors（57：人话映射全表）、free-engines（190：三引擎 fetch mock 全路径）、paste（40：条件恢复判定）、prompt（35：方向检测/构建/清洗）、providers（11 家预设结构）、sse（165：事件切分/双协议 delta/推理分离）、syntax（32：全文件语法冒烟）。**测试风格：fetchImpl 注入 + 固定夹具，不碰真网络**——与拾语测试纪律完全同构。

---

## 10. 与拾语对照（翻译能力矩阵）

| 维度 | Xtranslate | 拾语现状 | 差距判断 |
|---|---|---|---|
| 触发场景 | **输入处反向**（打中文出英文贴回去） | 划词/剪贴板**正向**（看懂别人写的） | **互补场景，拾语缺整块**（票 40） |
| 手停即译 + 防抖竞态 | ✅ 成体系（300ms/序号/abort/淡显/IME） | ❌ | **核心可借鉴**（票 40） |
| wantCommit 自动贴出 | ✅ | ❌ | 同上 |
| 免费引擎层（微软/谷歌/腾讯） | ✅ 零 key 开箱 | ❌ 单 DeepSeek 需 key | **第二短板**（票 41；与票 36 公共通道互补） |
| 微软 Bing token 会话工程 | ✅ 缓存/去重/预热/401 刷新 | ❌ | 随票 41 |
| 口语化 Prompt（few-shot 人设） | ✅ 极强（双向 5 组示例+七规则+清洗） | ❌ 通用翻译 prompt | **直接可搬**（票 40 同时提升正向） |
| 回贴原窗口（F24+条件恢复） | ✅ 踩坑完整 | 部分（SelectionCapture 借还，无强制前台序列） | 随票 40 |
| SSE 流式 | ✅ 双协议+推理分离 | ✅ SSE | 平（拾语多推理分离思路可互借） |
| 译文入库/批量/搜索 | ❌ | ✅ v10 一等公民 | 拾语优 |
| 方向自由度 | 仅中↔英 | 任意目标语言 | 拾语优 |
| 剪贴板条件恢复 | ✅ 内容比对 | 部分（借还无内容比对） | 借鉴 |
| API key per-provider 加密 | ✅ DPAPI+getPublic 剥离 | ❌ 明文仅不显示 | **可搬的配置升级** |
| 术语表/上下文 | ❌ | ❌ | 共同空白 |

**结论：拾语翻译"正向深度"（流式/入库/批量/回贴/多后端）全面领先；Xtranslate 的"反向输入交互 + 免费引擎层 + 口语 prompt + 密钥治理"是四块独立可搬的增量。**

---

## 11. 落点建议（拆票依据，按序）

1. **票 40（交互级）反向翻译输入框**：热键 Alt+Q（可配）呼出小框，打中文手停即出英文，Enter 贴回原输入框。搬：手停防抖状态机（300ms+竞态守卫+IME 感知+wantCommit）、prompt 双向口语人设（把现有 TranslationPrompt 扩出"口语模式"）。复用：SelectionCapture 记录目标窗口、面板浮窗基建、票 34 语种先验。回贴增强（GetGUIThreadInfo caret + AttachThreadInput+F24 强制前台 + 条件恢复）按 paste.js 细节补进 Win32 层。
2. **票 41（引擎级）免费引擎层**：微软 Bing 会话（token 缓存/预热/401 刷新，parseBingAuth 正则与夹具照搬思路）+ 谷歌 gtx（谨慎，灰色）+ 腾讯 TranSmart——作为拾语第二类后端 `FreeEngines`，与 36 公共通道、用户 key 组成"免费开箱 → 公共通道 → 自备 key"三档。注意：微软/谷歌均为非官方端点，与票 38 wontfix 的 Google 立场需用户裁决（微软 Bing 会话是"模拟网页自身请求"，合规风险低于伪装 UA 的 Google；本票建议默认只开微软）。
3. **票 42（质量+安全级）口语 Prompt 套件 + 密钥治理**：口语/标准双模式 prompt（正向反向共用）+ cleanOutput；**safeStorage 式 per-provider 密钥加密**（拾语可用 DPAPI 直接实现）+ getPublic 剥离——即使不建反向输入框，正向翻译质量与密钥安全也直接提升；与票 34 回声检测/低温天然组合。

> Xtranslate 的 MIT 许可允许直接借鉴代码细节（保留版权声明）；按拾语纪律建议参照重写为 C#，不移植 JS。**未读部分声明**：settings.html/settings.js（设置页 UI）、theme.css、dev-mock.js、docs/ 目录未逐行阅读——它们与本报告聚焦的"翻译功能核心链路"正交，不影响结论。
