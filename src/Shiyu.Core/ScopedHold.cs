namespace Shiyu.Core;

/// <summary>
/// 面板持有的"作用域热键"（Esc）的挂载点（票 41）。Esc 只在面板上屏期间才占，
/// 由热键注册表发一枚作用域句柄，释放句柄即注销——释放必须可靠，没释放的 Esc
/// 会被全系统吞掉。
///
/// 为什么需要 <see cref="Reacquire"/>：每次写设置都会广播 SettingsChanged，热键模块
/// 随之把注册表整体重建；面板持有的 Esc 跟着旧注册表一起被注销，而"已经持有"
/// 的标记不会让它自己重挂。面板里点「用免费引擎」会让这变成常规路径。
/// 修法是面板在场时在<b>新</b>注册表上重挂，顺序有两处要求：
/// <list type="number">
/// <item>重挂必须排在热键模块重建<b>之后</b>——否则挂上的是即将退役的旧注册表。
/// 这由 AppShell.HotkeysRebuilt 事件保证（重建完成才发）。</item>
/// <item>必须<b>先释放旧作用域，再取新的</b>。新旧注册表共用同一个消息窗口，id 都从 1
/// 开始编号；旧作用域释放得晚了，会把新注册的同号热键注销掉。</item>
/// </list>
/// </summary>
public sealed class ScopedHold(Func<IDisposable?> acquire)
{
    private IDisposable? _scope;

    /// <summary>此刻是否真的持有一枚作用域（没取到——被别的软件占着——就是 false）。</summary>
    public bool IsHeld => _scope is not null;

    /// <summary>没持有就去取一次；取不到不是错误（面板照常显示，点 ✕ 即可关闭），下次再试。</summary>
    public void Hold() => _scope ??= acquire();

    /// <summary>放手。幂等。</summary>
    public void Release()
    {
        var scope = _scope;
        _scope = null;
        scope?.Dispose();
    }

    /// <summary>热键注册表整体重建之后，把持有挪到新注册表上：先放旧的，再取新的。</summary>
    public void Reacquire()
    {
        Release();
        Hold();
    }
}
