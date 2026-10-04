# 20 — 浮层外壳统一与连接线 DPI

**来源：** UI 报告 U-02、U-03、§4.7；优化报告 O-30、O-31
**Blocked by:** 19（已完成）
**Branch:** `v3/shell`（两提交 aaf09e8/2a827da 已并 master cabd841）
**Status:** done（0.9.0 验收轮通过后发布；验收记录见文末）

实施+验收记录（2026-10-02）：四扇浮层外壳统一（Overlay 8/无边框/删自绘阴影/投影归 DWM；`Backdrop.Attach` verdict 回调，拒收走 `DegradeShell` 降级自绘描边+阴影+12 DIP 边距）；徽标胶囊 32 高+12 DIP 透明边距；连接线三修（`ScreenGeometry.ScaleForRect` 按目标显示器 DPI、先 Show 再压 z 序、锚窄条外缘+8 翻转）、曲线条件化 `ShouldDrawCurve`（Core 可测）否则 2 DIP 同色桥。新增探针两条（connector-inside-panel 曲线墨迹 0px、preview-bar-overlap 重叠 0px²）。**合入后 master 实测：872 测试全绿、探针 21/22（panel-double-radius 7.9 DIP 单弧转绿，唯一红=票 22 英文异常）、双静态检查绿**。余手动项：双屏拖窄条 20 次看曲线落缝（用户实机）。

**What to build:**
- 窄条、预览、翻译面板、快速条的外壳：圆角 8，边框和阴影交给 DWM（需要令牌色时用 `DWMWA_BORDER_COLOR`），`Shell BorderThickness=0`，删除 `Shell.Effect`。`Backdrop.Attach` 返回是否成功；失败时才自绘描边和阴影，并留 12 DIP 透明边距。
- 复制徽标：窗口留 12 DIP 透明边距（胶囊不是矩形，是家族里唯一由 WPF 画阴影的窗口）。
- 连接线：按**目标显示器**取 DPI（`MonitorFromRect` + `GetDpiForMonitor`）；先 `Show()`，再定位并压到预览之下。预览改锚窄条外缘 + 8，两窗永不重叠。
- 只在预览与卡片错位超过 24 DIP、或缝隙超过 16 DIP 时才画连接线，否则画 2 DIP 的同色桥。

**验收：**
- [ ] 四个浮层的角部像素只有一条弧（150% 缩放下半径 12 px ±1）
- [ ] 在 150% 主屏与 100% 副屏之间来回拖动窄条 20 次，预览矩形内的曲线像素数始终为 0
- [ ] 任何位置下，预览与窄条的重叠面积为 0
- [ ] 滚动窄条时 GPU 占用下降（任务管理器观察）

**验收（2026-10-04，v3 收官）：** 用户在本机走完 0.9.0 验收轮——accept3 → accept18 构建、走查四至七轮；docs/manual-test-v5.md 是发版闸门（"全部通过即可发 v0.9.0 正式版"），验收中发现的问题已在发布前修复（如 B2 d2dd0ff、B4 f94f77e）——随后发布 0.9.0 / 0.9.1，用户确认关闭本票。机器可测部分：探针 28/0/1、测试全绿。关闭前的状态：ready-for-human
