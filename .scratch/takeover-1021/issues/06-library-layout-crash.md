# 06 — 虚拟化列表量尺寸时抛「高度必须为非负值」

**来源：** 用户日志 2026-10-19 14:12（当时是 0.17.1）
**Status:** needs-info（等下一次发生，用票 04 之后的完整栈定位）

**现象：** `AppCrash ex=ArgumentException: 高度必须为非负值。`，栈顶 `VirtualizingStackPanel.ExtendViewport` ←
`IsExtendedViewportFull`。UI 线程的全局兜底接住了（`Handled = true`），进程没死，用户看到托盘一句「拾语遇到一个错误」。
前后 20 秒内日志只有两次 SettingsSaved，没有复制。

**已知的：**
- 首帧的参数 `IHierarchicalVirtualizationAndScrollInfo` 是分组（层级）虚拟化才走的路。全仓库开了分组虚拟化的只有
  历史窗口的主列表 `LibraryList`（`IsVirtualizingWhenGrouping` + Recycling，行高按数据写 56 / 72）——头号嫌疑，**未证实**。
- 另外两个像素滚动的虚拟化列表：翻译记录页 `LogList`、窄条列表（CacheLength 1 页）。
- 日志原来只留三帧 WPF 内部签名，看不出是哪个列表；票 04 之后能看到 `GroupItem` 这一层与应用自己的帧。

**下一步：** 再发生一次就有足够的栈。若证实是 `LibraryList`，候选修法是改动分组/重载时机或把缓存单位换成 Item，
修之前先用探针在分组切换与批量删除时复现。

## Comments
