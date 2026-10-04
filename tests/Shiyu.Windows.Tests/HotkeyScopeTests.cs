using Shiyu.Windows;

namespace Shiyu.Windows.Tests;

/// <summary>
/// 作用域热键与注册表退役的交界（票 41）。设置每保存一次，热键注册表就整体重建：
/// 旧注册表 Dispose 时已把自己的 id（含面板 Esc 那枚）全部注销，新注册表同一个
/// 消息窗口、id 又从 1 编号。此后旧作用域若再按 id 注销一次，注销掉的就是新注册表
/// 里恰好同号的热键——面板里点按钮写设置、再按 Esc 的那条常规路径上，丢的可能是
/// Esc，也可能是别的常驻热键。
///
/// 全程用假的"操作系统"（见 internal 构造函数的注册/注销两个委托），不碰真实的
/// 全局热键——与同目录其余测试一样，可以和用户正在跑的实例并排安全地跑。
/// </summary>
public sealed class HotkeyScopeTests
{
    /// <summary>只记得"哪个窗口上的哪个 id 当前有效"——这正是 Win32 热键表对本缺陷的全部意义。</summary>
    private sealed class FakeHotkeyTable
    {
        public HashSet<(IntPtr Window, int Id)> Registered { get; } = [];

        public bool Register(IntPtr window, int id, uint modifiers, uint key) => Registered.Add((window, id));

        public void Unregister(IntPtr window, int id) => Registered.Remove((window, id));
    }

    private static HotkeyRegistry Open(MessageWindow window, FakeHotkeyTable table)
        => new(window, table.Register, table.Unregister);

    private static Hotkey Plain(string name) => new(HotkeyModifiers.Control, 0x41, name);

    private static Hotkey Escape => new(HotkeyModifiers.None, 0x1B, "关闭面板");

    [Fact]
    public void A_scope_released_after_its_registry_retired_leaves_a_reissued_id_alone()
    {
        using var window = new MessageWindow();
        var table = new FakeHotkeyTable();

        // 旧注册表：一个常驻热键（id 1）+ 面板的 Esc（id 2）。
        var old = Open(window, table);
        Assert.Null(old.Register(Plain("常驻"), () => { }));
        var escape = old.TryRegisterScoped(Escape, () => { });
        Assert.NotNull(escape);

        // 写设置：旧注册表退役，新注册表重新编号——它的两个常驻热键占了 id 1 和 2。
        old.Dispose();
        var fresh = Open(window, table);
        Assert.Null(fresh.Register(Plain("常驻一"), () => { }));
        Assert.Null(fresh.Register(Plain("常驻二"), () => { }));

        // 面板此刻才放掉旧的 Esc 作用域：它的 id 2 现在是新注册表的"常驻二"。
        escape!.Dispose();

        Assert.Contains((window.Handle, 1), table.Registered);
        Assert.Contains((window.Handle, 2), table.Registered);
        fresh.Dispose();
    }

    [Fact]
    public void A_scope_released_while_its_registry_lives_unregisters_normally()
    {
        using var window = new MessageWindow();
        var table = new FakeHotkeyTable();
        var registry = Open(window, table);
        var escape = registry.TryRegisterScoped(Escape, () => { });
        Assert.Contains((window.Handle, 1), table.Registered);

        escape!.Dispose();

        Assert.Empty(table.Registered);
        registry.Dispose();
    }

    [Fact]
    public void Releasing_a_scope_twice_is_harmless()
    {
        using var window = new MessageWindow();
        var table = new FakeHotkeyTable();
        var registry = Open(window, table);
        var escape = registry.TryRegisterScoped(Escape, () => { });

        escape!.Dispose();
        table.Registered.Add((window.Handle, 1));   // 同号 id 又被别人占了
        escape.Dispose();

        Assert.Contains((window.Handle, 1), table.Registered);
        registry.Dispose();
    }

    [Fact]
    public void Retiring_a_registry_unregisters_everything_it_registered_including_scopes()
    {
        using var window = new MessageWindow();
        var table = new FakeHotkeyTable();
        var registry = Open(window, table);
        Assert.Null(registry.Register(Plain("常驻"), () => { }));
        Assert.NotNull(registry.TryRegisterScoped(Escape, () => { }));

        registry.Dispose();

        Assert.Empty(table.Registered);
    }

    [Fact]
    public void A_scope_refused_by_the_system_comes_back_null()
    {
        using var window = new MessageWindow();
        var registry = new HotkeyRegistry(window, (_, _, _, _) => false, (_, _) => { });

        Assert.Null(registry.TryRegisterScoped(Escape, () => { }));
        registry.Dispose();
    }
}
