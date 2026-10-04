# 18 — UI 根因：隐式文字样式、窗口根字号、文字透明度

**来源：** UI 报告 §2（R1、R4、R6）、U-01；优化报告 O-28
**Blocked by:** —
**Branch:** `v3/ui-root`（已并 master，218d3f5；合并后全套 706/706）
**Status:** done（0.9.0 验收轮通过后发布；验收记录见文末）

**What to build:**
- 删除 App 级隐式 `TextBlock` 样式里的 `Foreground` 和 `FontFamily`（`Themes/Controls.xaml:16-19`），改在每个窗口根元素上设置 `TextElement.FontFamily / FontSize / Foreground`。这一条同时修好标题栏字形变豆腐块、删除钮危险色失效、分段选中文字只有 3.0:1、面板禁用按钮"灰盒子配黑字"。
- 设置窗根节点设字号，消灭 12 的回落（R4）。
- 文字上的 `Opacity` 字面量全部去掉，改用颜色令牌（R6，说明文字现在只有约 3.5:1）。

**验收：**
- [x] 形式：全仓 grep 不到隐式 TextBlock 样式里的 Foreground/FontFamily；设置窗根有字号（`TextElement.FontSize=Size.Secondary`，与标签同档，消灭"值比标签小"的倒置）；文字无 Opacity 字面量（`TextTokenHygieneTests` 白名单除外，现存三条均为装饰：连接曲线描边、深链水洗层、预览阴影）
- [x] 构建实跑：Core 测试全绿 688/688（含新增对比度对与两条静态检查）；App Debug 构建 0 错误（仅既有 CS8629 警告）
- [x] 像素级（部分，2026-10-02 探针实例 PrintWindow + ASCII 墨迹图）：管理窗与设置窗标题栏三钮渲染为三个**互不相同**的正确字形（X、方框、横线）——修复前是三个相同的豆腐空框；正文/按钮文字全部正常着墨（TextElement 根前景未丢内容）
- [ ] 像素级（余）：分段选中文字对比度 ≥ 4.5:1（需选中态）；删除钮 hover 时呈 Danger 色（需 hover 态）——待票 15 探针脚本覆盖状态态后执行

**完成记录（2026-10-01，v3/ui-root）：**
- R1：`Themes/Controls.xaml` 的隐式 `TextBlock` 样式整体删除（留注释说明理由）；七个 XAML 窗（Bar/Settings/Library/Panel/Preview/QuickBar/Badge）根元素改设 `TextElement.FontFamily / FontSize / Foreground`；代码建窗（`OnboardingWindow`、`GroupManagerWindow`、`UpdateWindow`、`BackupUi` 三处对话框）补根级 `TextElement` 前景（UpdateWindow 连字体一并补齐）。
- R4：设置窗根设 `TextElement.FontSize=Size.Secondary`（页签/输入值/底栏按钮从回落 12 提到与标签同档 15，层级不再倒置）；`OnboardingWindow` 手写 13/14/18 收敛为 `Size.Caption/Size.Hint/Size.BodyLarge` 引用；`ItemEditors` 两处手写 14 改 `Size.Hint`；`ActionTray` 字形 16 改 `Size.IconMedium`。
- R6：删除所有作用于文字的 `Opacity` 字面量，分层改用令牌色——`ItemEditors`（两处 0.75）、`SettingsWindow.xaml.cs`（0.8/0.75/0.7/0.75，其中搜索结果面包屑补 `Brush.TextSecondary`）、`OnboardingWindow`（七处）、`GroupManagerWindow`（三处）、`BarWindow.xaml` 与 `PreviewWindow.xaml.cs` 的失效路径改 `Brush.TextTertiary`；`PanelWindow` 词典卡例句/同义词由 `TextTertiary` 改 `TextSecondary`（该组合实测 4.33:1）。
- 测试：`ReadablePairs` 补 `(TextTertiary, SurfaceSubtle)`（先红 4.33 后修；`(TextSecondary, Surface)` 早已在表）与浅色 `TextTertiary` 由 `#FF62707B` 加深至 `#FF5E6C77`（4.59:1）；新增 `TextTokenHygieneTests` 两条静态检查（Themes/ 外禁文字 Opacity 小数字面量与数字 FontSize，装饰走按"路径+行内标记"匹配的白名单），已用注错探针验证会红。总数 686 → 688。
- 目检受限：单实例互斥量仍是固定名 `Local\Shiyu`（票 15 的探针互斥尚未合入本分支），按票面纪律不启动 exe，目检待探针合入后补做。

**验收（2026-10-04，v3 收官）：** 用户在本机走完 0.9.0 验收轮——accept3 → accept18 构建、走查四至七轮；docs/manual-test-v5.md 是发版闸门（"全部通过即可发 v0.9.0 正式版"），验收中发现的问题已在发布前修复（如 B2 d2dd0ff、B4 f94f77e）——随后发布 0.9.0 / 0.9.1，用户确认关闭本票。机器可测部分：探针 28/0/1、测试全绿。关闭前的状态：ready-for-human（代码与测试已落，余像素级探针验收）
