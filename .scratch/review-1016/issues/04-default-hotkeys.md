# 04 — 新安装的默认热键换成 Win+Alt 组

**来源：** 评审 F-04 · D2
**Status:** ready-for-agent

**What to build:** 新安装默认：划词 Win+Alt+Z、翻译剪贴板 Win+Alt+X、快速粘贴 Win+Alt+V、反向输入 Win+Alt+Q。老用户的设置文件里缺这几项时仍按旧默认读（不能悄悄换掉老用户的键）。引导呼出屏加一行「这些键会盖掉其他应用里的同名快捷键」，对已知冲突（Ctrl+Shift+V / Z、Alt+Q）就地提示。CHANGELOG 已知问题相应改写。

**验收：** 全新数据目录启动后热键是 Win+Alt 组；带旧设置文件启动热键不变；引导里改键即生效。
