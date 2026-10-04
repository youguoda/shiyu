# 27 — 可访问性基线

**来源：** UI 报告 U-29、§4.8；优化报告 O-35
**Blocked by:** 23、24（均已完成）
**Branch:** `v3/a11y27`（已并 master ae4945a）
**Status:** done（0.9.0 验收轮通过后发布；验收记录见文末）

实施+验收记录（2026-10-03，代理主体+主控收尾）：图标钮/编辑器 `AutomationProperties.Name` 全补（名称单源=KeyMap 与卡片标签；开关名绑卡片标签——设置窗 21 交互件 3 无名→**0**）；InfoBar ContentControl peer 可读（票 22 留尾）；错误 `LiveSetting=Assertive`；TextScaleFactor 跟随（UISettings→ThemeManager 出口乘 Type.* 全档）；探针 a11y 断言五条（已知名/全件具名扫描含 ScrollBar 模板件豁免注明/管理窗键盘流等）。**938+3 测试绿、探针 28 PASS/0 FAIL/1 SKIP、双静态绿**。

**What to build:**
- 所有图标按钮设置 `AutomationProperties.Name`（现在全仓为 0 处）；状态变化（已复制、错误）用 `LiveSetting` 朗读。
- 焦点视觉：双层焦点环（`Focus.Outer` 2 DIP + `Focus.Inner` 1 DIP，圆角 = 控件圆角 + 2）。现在全仓 `FocusVisualStyle` 为 0 处。
- 对比度测试覆盖实际画出来的组合；非文本对比（输入框边界、开关描边）≥ 3:1。
- 跟随系统"辅助功能 › 文本大小"（`UISettings.TextScaleFactor`），作用于内容字号与控件字号。
- 尊重"减少动画"的现有机制保持不变。

**验收：**
- [ ] 讲述人能读出每个图标按钮的名字
- [ ] 每个窗口里按 Tab 遍历，焦点处处可见
- [ ] 系统文本大小调到 150% 时布局不重叠、不截断

**验收（2026-10-04，v3 收官）：** 用户在本机走完 0.9.0 验收轮——accept3 → accept18 构建、走查四至七轮；docs/manual-test-v5.md 是发版闸门（"全部通过即可发 v0.9.0 正式版"），验收中发现的问题已在发布前修复（如 B2 d2dd0ff、B4 f94f77e）——随后发布 0.9.0 / 0.9.1，用户确认关闭本票。机器可测部分：探针 28/0/1、测试全绿。关闭前的状态：ready-for-human（余讲述人实机冒烟与系统文本大小 150% 目验）
