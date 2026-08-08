using System.Runtime.InteropServices;

namespace AiMemoryManager.Services;

/// <summary>
/// FR-8.5 全局热键:Win32 RegisterHotKey/UnregisterHotKey 注册到主窗口句柄,
/// HwndSource.AddHook 挂 WndProc 分发 WM_HOTKEY → Pressed 事件。
/// 注册失败(热键被别的程序占用)返回 false,调用方降级处理,不抛异常。
/// </summary>
public class HotkeyService : IDisposable
{
    public const int HotkeyId = 0xA401;
    private const int WM_HOTKEY = 0x0312;

    public const int MOD_ALT = 0x1;
    public const int MOD_CONTROL = 0x2;
    public const int MOD_SHIFT = 0x4;
    public const int MOD_WIN = 0x8;

    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _hwnd;
    private bool _registered;

    /// <summary>热键按下(WndProc 收到 WM_HOTKEY 且 wParam==HotkeyId 时触发,运行在窗口 UI 线程)。</summary>
    public event EventHandler? Pressed;

    /// <summary>是否已注册成功( false = 热键被占用或未注册,仅静默降级)。</summary>
    public bool IsRegistered => _registered;

    /// <summary>注册热键;重复调用先注销旧的。返回 false 表示被其他程序占用。</summary>
    public bool Register(IntPtr hwnd, int modifiers, int key)
    {
        Unregister();
        _registered = RegisterHotKey(hwnd, HotkeyId, (uint)modifiers, (uint)key);
        _hwnd = _registered ? hwnd : IntPtr.Zero;
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) { UnregisterHotKey(_hwnd, HotkeyId); _registered = false; _hwnd = IntPtr.Zero; }
    }

    /// <summary>HwndSource 钩子:命中 WM_HOTKEY + HotkeyId 时置 handled 并触发 Pressed,其余消息透传。</summary>
    public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    /// <summary>把 Win32 修饰符 + VK 格式化为显示文本,如 "Ctrl+Shift+M"。</summary>
    public static string Format(int modifiers, int key)
    {
        var parts = new List<string>(4);
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(System.Windows.Input.KeyInterop.KeyFromVirtualKey(key).ToString());
        return string.Join("+", parts);
    }

    public void Dispose() => Unregister();
}
