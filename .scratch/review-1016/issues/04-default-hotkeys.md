# 04 — 新安装的默认热键换成 Win+Alt 组

**来源：** 评审 F-04 · D2
**Status:** ready-for-human

**What to build:** 新安装默认：划词 Win+Alt+Z、翻译剪贴板 Win+Alt+X、快速粘贴 Win+Alt+V、反向输入 Win+Alt+Q。老用户的设置文件里缺这几项时仍按旧默认读（不能悄悄换掉老用户的键）。引导呼出屏加一行「这些键会盖掉其他应用里的同名快捷键」，对已知冲突（Ctrl+Shift+V / Z、Alt+Q）就地提示。CHANGELOG 已知问题相应改写。

**验收：** 全新数据目录启动后热键是 Win+Alt 组；带旧设置文件启动热键不变；引导里改键即生效。

## Comments

- 2026-10-16 实现：默认 Win+Alt 组；TryParse 对文件里缺的键补旧默认（写了的原样保留）；HotkeySpec 写法 Win 在前；HotkeyConflicts 列常见冲突，设置与引导的录键处就地提醒；「恢复全部默认」恢复的是新一组。实机：四个 Win+Alt 组合在本机 RegisterHotKey 全部成功。
- **待你实机验收**：全新数据目录启动 → 热键是 Win+Alt 组；带旧设置文件启动 → 热键不变；设置里录 Ctrl+Shift+V → 下面出现冲突提醒。
