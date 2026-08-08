using System.Windows.Interop;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class HotkeyServiceTests
{
    private const int WM_HOTKEY = 0x0312;

    [Fact] public void WM_HOTKEY消息触发Pressed事件()
    {
        var s = new HotkeyService();
        int fired = 0;
        s.Pressed += (_, _) => fired++;
        bool handled = false;
        s.WndProc(IntPtr.Zero, WM_HOTKEY, new IntPtr(HotkeyService.HotkeyId), IntPtr.Zero, ref handled);
        Assert.Equal(1, fired);
        Assert.True(handled);
    }

    [Fact] public void 其他消息不触发()
    {
        var s = new HotkeyService();
        int fired = 0;
        s.Pressed += (_, _) => fired++;
        bool handled = false;
        s.WndProc(IntPtr.Zero, 0x0200 /*WM_MOUSEMOVE*/, IntPtr.Zero, IntPtr.Zero, ref handled);
        Assert.Equal(0, fired);
        Assert.False(handled);
    }

    [Fact] public void 未注册时Register返回依赖系统_测试中跳过真实注册()
    {
        // 仅验证 Dispose/Unregister 幂等不抛
        var s = new HotkeyService();
        s.Unregister();
        s.Dispose();
        s.Dispose();
    }
}
