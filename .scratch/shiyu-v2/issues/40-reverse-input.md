# 40 — 反向翻译输入框：热键呼出，打中文出英文，Enter 贴回原处

**What to build:** 落实 Xtranslate 调研（docs/research/2026-09-27-xtranslate-analysis.md
§1/§2/§7.1）的核心交互——"反向翻译输入法"：在任何输入框按 **Alt+Q**（可配）呼出
小浮窗，直接打中文，**手停 300ms 自动出英文**（LLM 流式逐字），**Enter 把译文贴回
原输入框**，Esc 关闭。解决"回英文帖要开翻译→复制→切回→粘贴五步"的场景。
**这是与现有正向翻译互补的新场景**（不是重复面板）：正向是"看懂别人"，反向是
"回复别人"。

## 交互规格（照搬 Xtranslate 验证过的状态机）

- Alt+Q：记录当前前台窗口句柄（SelectionCapture）→ 浮窗出现在**目标输入框
  文字光标处**（GetGUIThreadInfo caret，拾语已有等价 caret 逻辑）→ 聚焦输入。
- 打字手停 300ms → 翻译（走现有 ITranslationBackend 管线，LLM 流式逐字显示）；
  **竞态守卫**：新输入取消旧请求（AbortController + 序号），等待期旧译文淡显。
- **IME 感知**：`isComposing/keyCode 229` 的 Enter/Esc 交给输入法。
- **Enter** 贴回：译文未到则标记 wantCommit 自动等；到了立即
  隐藏浮窗 → 强制前台回目标窗口 → 模拟 Ctrl+V → **条件恢复剪贴板**
  （仅当剪贴板仍是刚写的译文才还原快照；400ms 后执行）。
  强制前台采纳 Xtranslate 的 F24 技巧（不发 Alt——会激活菜单栏）。
- **Esc** 关闭；**Tab** 循环方向（自动/中→英/英→中，本地码位检测）；
  **Ctrl+E** 切换翻译模式（口语/标准）。
- 浮窗随内容长高（80–800 钳制），贴光标下方、空间不足翻上方（复用
  BadgePlacement/caret 基建）。

## Prompt（口语人设，本票同时提升正向质量）

把现有通用翻译 prompt 扩展为**口语对话模式**（Xtranslate prompt.js 的人设与
few-shot 结构，按拾语纪律参照重写）："中英双语地道的朋友帮用户打字"，带
5 组 few-shot、防"回答问题"（`<text>` 包裹）、语气对等、错别字容错、
cleanOutput 剥包装。设置里提供"翻译风格：标准/口语"选择（口语用于反向输入
框默认，正向面板可选）。

## 复用与新增边界

- 复用：SelectionCapture（记目标 hwnd）、翻译管线（ITranslationBackend/SSE）、
  浮窗基建（非激活、置顶、失焦隐藏、Placement）、票 34 语种先验。
- 新增：ReverseInputWindow（或 PanelWindow 加反向模式）、手停防抖状态机、
  回贴链路（强制前台 + F24 + 条件恢复，在 SelectionCapture/新 Win32 类里）、
  口语 prompt 套件。
- 默认热键 Alt+Q 可在设置修改（冲突检测沿用现四键机制）。

**价值:** 打通"回复英文帖/写英文邮件/代码评论"场景——拾语从阅读工具变为
写作工具。

**Blocked by:** 34（语种先验与低温 prompt 是方向判定与质量地基）。

**Status:** ready-for-agent

- [ ] ReverseInputWindow：光标处呼出/失焦隐藏/内容长高/IME 感知
- [ ] 手停 300ms 防抖 + 请求竞态守卫（序号+abort）+ 等待期淡显
- [ ] 回贴链路：强制前台（F24 技巧，不发 Alt）→ Ctrl+V → 条件恢复剪贴板
- [ ] wantCommit：翻译中按 Enter 自动等译文到达后贴出
- [ ] 口语 prompt 套件（双向 few-shot + `<text>` 包裹 + cleanOutput）+
      设置"翻译风格"选项（同时可用于正向面板）
- [ ] Alt+Q 热键设置项（冲突检测）
- [ ] 单测：方向检测/cleanOutput/防抖状态机（Core 假时钟）
- [ ] E2E 探针：记事本输入 → Alt+Q → 打中文 → 手停出英文 → Enter →
      断言记事本内容（UIA 读回）
