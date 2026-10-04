namespace Shiyu.Windows;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,

    /// <summary>
    /// Stops the hotkey auto-repeating while held. Without it, holding the keys
    /// half a second fires capture over and over.
    /// </summary>
    NoRepeat = 0x4000,
}

public sealed record Hotkey(HotkeyModifiers Modifiers, uint Key, string Description);

/// <summary>
/// Registration failed, almost always because another application already owns
/// the combination.
/// </summary>
public sealed record HotkeyConflict(Hotkey Hotkey, string Message);

/// <summary>
/// Owns Shiyu's global hotkeys and routes their messages back to whoever asked
/// for them.
/// </summary>
public sealed class HotkeyRegistry(MessageWindow window) : IDisposable
{
    private readonly MessageWindow _window = window;
    private readonly Dictionary<int, Action> _handlers = [];

    /// <summary>
    /// 向系统注册 / 注销的出口。生产就是 Win32 的 RegisterHotKey / UnregisterHotKey；
    /// 只有测试经 internal 构造函数换成假的"热键表"（票 41：作用域与注册表退役的
    /// 交界要有测试，而测试不能去占真实的全局热键）。
    /// </summary>
    private readonly Func<IntPtr, int, uint, uint, bool> _register = NativeMethods.RegisterHotKey;

    private readonly Action<IntPtr, int> _unregister =
        static (handle, id) => NativeMethods.UnregisterHotKey(handle, id);

    private int _nextId = 1;
    private bool _listening;
    private bool _disposed;

    internal HotkeyRegistry(
        MessageWindow window, Func<IntPtr, int, uint, uint, bool> register, Action<IntPtr, int> unregister)
        : this(window)
    {
        _register = register;
        _unregister = unregister;
    }

    /// <summary>
    /// Registers a hotkey, returning null on success or what went wrong.
    ///
    /// A conflict is reported rather than thrown: losing one hotkey to another
    /// application is a normal thing to have happen, and it must not stop the
    /// rest of Shiyu from starting.
    ///
    /// After <see cref="Dispose"/> the call throws: the registry is rebuilt on
    /// every settings save (O-20), and a window still holding the retired one
    /// must find out loudly instead of silently re-arming a hotkey whose owner
    /// is gone (O-43).
    /// </summary>
    public HotkeyConflict? Register(Hotkey hotkey, Action onPressed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureListening();

        var id = _nextId++;
        var modifiers = (uint)(hotkey.Modifiers | HotkeyModifiers.NoRepeat);

        if (!_register(_window.Handle, id, modifiers, hotkey.Key))
        {
            return new HotkeyConflict(
                hotkey,
                $"快捷键「{hotkey.Description}」已被其他软件占用，拾语的这项功能暂时无法使用。");
        }

        _handlers[id] = onPressed;
        return null;
    }

    /// <summary>
    /// Registers a hotkey only for as long as the returned handle is held, or
    /// returns null if it is already taken.
    ///
    /// Exists for keys Shiyu has no business owning all the time — Escape,
    /// while a panel is on screen. Disposing must be reliable: an Escape key
    /// left registered would be swallowed system-wide. Like
    /// <see cref="Register"/>, retired registries refuse loudly (O-43).
    /// </summary>
    public IDisposable? TryRegisterScoped(Hotkey hotkey, Action onPressed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureListening();

        var id = _nextId++;
        var modifiers = (uint)(hotkey.Modifiers | HotkeyModifiers.NoRepeat);

        if (!_register(_window.Handle, id, modifiers, hotkey.Key))
        {
            return null;
        }

        _handlers[id] = onPressed;
        return new Scope(this, id);
    }

    private sealed class Scope(HotkeyRegistry registry, int id) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            // 注册表已退役：它 Dispose 时就把自己的 id（含这一枚）全注销了。新注册表
            // 共用同一个消息窗口、id 又从 1 编号，这个 id 此后可能已被发给别的热键
            // ——再按 id 注销一次，注销掉的就是别人的（票 41：面板在场时写设置，
            // 旧作用域放得晚了，丢的是新注册的同号热键，Esc 或某个常驻键）。
            if (registry._disposed)
            {
                return;
            }

            registry._unregister(registry._window.Handle, id);
            registry._handlers.Remove(id);
        }
    }

    private void EnsureListening()
    {
        if (_listening)
        {
            return;
        }

        _window.MessageReceived += OnMessage;
        _listening = true;
    }

    private void OnMessage(WindowMessage message)
    {
        if (message.Id != NativeMethods.WmHotkey)
        {
            return;
        }

        if (_handlers.TryGetValue(message.WParam.ToInt32(), out var handler))
        {
            message.Handle();
            handler();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var id in _handlers.Keys)
        {
            _unregister(_window.Handle, id);
        }

        _handlers.Clear();

        if (_listening)
        {
            _window.MessageReceived -= OnMessage;
            _listening = false;
        }
    }
}
