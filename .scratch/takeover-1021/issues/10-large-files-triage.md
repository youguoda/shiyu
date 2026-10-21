# 10 — 其余大文件：拆不拆、怎么拆

**来源：** 接手摸底
**Status:** needs-triage

**候选（物理行）：** `PanelWindow.xaml.cs` 1614 · `LibraryWindow.xaml.cs` 1314 · `BarInteractions.cs` 1155 · `OnboardingWindow.cs` 1152 ·
`PreviewWindow.xaml.cs` 970 · `LibraryDetail.cs` 921。`App.DebugProbes.cs` 1756 行只在 Debug 下编译，不在此列。

**分诊要回答的：** 每个文件里有没有能独立成类、并且有探针或测试守着的一块；没有守护的先补守护再拆。
票 08、09 做完、看过搬家的成本之后再定。

## Comments
