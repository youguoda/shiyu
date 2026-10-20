# 29 — 预览工作台全面测试（用户实录 2026-10-20：叠显/串台/图片卡顿）

**Status:** ready-for-human

**What to build:** 预览弹窗（钉住的「工作台」）的严谨测试面：复现用户报告的三类问题
（新旧内容叠显、跨条串台、图片缩放卡顿），并压力/性能多方面扫未见的问题。先测试出报告，
修复另定。

**交付（全部入库）：**
- PreviewWindow 探针钩子 ~25 个（草稿/芯片/缩放/指针合成事件读数）。
- 新探针五枚（全进 run-all）：preview-workbench（17 检查）、preview-anchor（3，动真光标、
  Test-UserPresent 守卫）、preview-zoom-perf（5，Input 针 + Dispatcher.Hooks + 帧间隙）、
  preview-decode-cost（3）、preview-churn（4）。
- PreviewSizingTests +2 事实（4K 解码算术，绿）。

**run-all 195 检查：PASS 183 / FAIL 11 / SKIP 1。** 红的全是缺陷复现（defect:* 约定）：

| 缺陷（复现数值） | 根因（file:line，PreviewWindow.xaml.cs 除注明外） |
|---|---|
| 只读正文与编辑框叠印（True/True） | TeardownWorkbench 死代码零调用 :452-461；Fill 不复位 :711-807 |
| A 的草稿串到 B（draftIsA=True len=18） | 同上——EditHost.Text 跨显示存活；WorkbenchText :512 读残留框 |
| 残留芯片压教学行（chips=True teach=True） | ActionRow 与 TeachRow 同槽（XAML :112-152），Fill 只开教学行不清芯片 |
| 下一张图带着上次缩放/平移（scale=2.44 origin≈2283;2271） | _imageScale/_imageOrigin 只在死代码里复位 :458-459 |
| 强拆后图钉仍显「已钉」（pinned=False glyph=full） | RemoveCard 路径不走 UpdatePinVisual（BarCards.cs:762-766） |
| 元信息行永久消失（when/kind 均 False） | SetupWorkbench :448-449 收起后 Fill 从不恢复 |
| 图钉反转：视觉已钉、首击才真钉（before=False glyph=full after=True） | 上一条的直接后果 |
| Esc 不退出编辑（editStillVisible=True） | PreviewWindow 无任何 KeyDown 处理（commit 418ea30 宣称有） |
| 双击不复位（scale=5.96） | OnWorkbenchDoubleClick :591-602 从未接线（XAML :14 只有 MouseEnter/Leave） |
| 缩放锚点漂移 28.5 DIP（固定光标下图像点位移） | OnWorkbenchWheel :530-536 用 GetPosition(ImageHost)（图像局部空间）算父空间的 origin |
| **预览动作芯片从未工作**（clicked=True fired=0，五钉后仍 0） | 处理器判 `sender is Button`——路由事件里 sender 是挂 handler 的 ActionRow，按钮在 OriginalSource 里；类型模式恒假（新发现的功能缺陷） |

**性能面（绿，但有一项边缘）：**
- 4K 原图首显冷 52ms / 暖 7ms，解码全程 UI 针 ≤50ms——解码不是瓶颈。
- 4K 缩放/平移全顺（inputMax ≤83ms、帧隙 ≤31ms）；显/收搅拌 30 轮工作集 0MB、句柄 +0。
- 小图（900×300）缩放三次运行 inputP95 = 60 / 57 / 46ms（预算 50）——**边缘、抖动**，
  且反直觉地差于 4K；修复期查（疑与原图中途落位换源/逐拍变换重建有关），暂记边缘项。
- 卡顿的结构性嫌疑仍在渲染面：HighQuality（Fant）每拍重滤波（XAML :97）+ 每拍新建
  TransformGroup（:515-521）+ 分层窗整幅合成——预算内但修复期值得做（平移/缩放手感）。

**修复期提案（等用户点头，本次未动任何产品代码）：**
1. Unpin/TakeDown(force) 调 TeardownWorkbench（或 Fill 进场防御性复位全表）。
2. 芯片 handler 改用 `e.OriginalSource`（或模板内按钮直接挂 Click）——顺手让芯片真的能用。
3. 接线双击复位；编辑框加 Esc 退出；强拆路径补 UpdatePinVisual。
4. 滚轮锚点：统一父空间换算（Wheel 后 origin 用 e.GetPosition(宿主) 而非 ImageHost）。
5. 交互期 BitmapScalingMode 降 Linear + 复用缓存变换组（卡顿手感）。
6. 提纯件：Shiyu.Core/WorkbenchChipTable + PreviewImageTransform（缩放数学可单测）。

probe-* 五枚已入 run-all；探针脚本 ASCII、PS 5.1；不碰真剪贴板。

## 修复（2026-10-20，用户「开始修改」）

- 全部 11 项 defect 转绿：run-all 195 检查 **PASS 194 / FAIL 0 / SKIP 1**。
- 根修：TakeDown 收尾统一调 TeardownWorkbench（草稿一并清空）+ UpdatePinVisual（强拆也走）——叠印/草稿串台/芯片残留/缩放残留/图钉过期/元信息消失/首击反转一起消。
- 芯片 handler 改判 `args.OriginalSource` 且只登记一次——芯片第一次能用（clicked=True fired=1），handler 不再累积（5 钉后一击一发）。
- 双击复位接线（登记在拖拽前：ClickCount≥2 或同点 500ms 两按，复位并取消拖拽）；编辑框 Esc 退出编辑、留在只读展示。
- 滚轮锚点改图像内容坐标公式（origin += u×(s−s′)）：残漂 28.5 → 2.6（父空间版，差布局偏移项）→ **0.273 DIP**。放大拖动改父空间逐段累计（原实现在放大档多段拖动会飞图）。
- 交互期 BitmapScalingMode 降 Linear + 变换组构造一次原地改值：小图缩放 inputP95 60/57/46 → 14–22ms；一比一展示仍 HighQuality（probe-preview-image 像素对齐保持全绿）。
- 提纯件：Shiyu.Core/WorkbenchChips（芯片表，7 facts）+ PreviewImageTransform（缩放算术，4 facts）；Core 2120 / Windows 26 全绿；三道门禁与 PUA 扫描干净。
