// HotkeyRegistry 的注册/注销出口是 internal 的测试接缝：作用域热键与注册表
// 退役的交界（票 41）要有测试，而测试不能去占真实的全局热键。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Shiyu.Windows.Tests")]
