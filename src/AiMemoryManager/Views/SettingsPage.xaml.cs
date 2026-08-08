using System.Windows.Controls;
using System.Windows.Input;
using AiMemoryManager.Services;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Views;

public partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    /// <summary>
    /// FR-8.5 热键捕获:只读输入框里按组合键,修饰键转 Win32 MOD_*,键转 VK 后写入设置并重新注册。
    /// 纯修饰键/无修饰键的组合直接忽略(等用户按完整组合)。
    /// </summary>
    private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;   // Alt 组合走 SystemKey
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;   // 纯修饰键,等完整组合

        int mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyService.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyService.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyService.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyService.MOD_WIN;
        if (mods == 0) return;   // 全局热键必须带修饰键,裸键忽略

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        ((SettingsViewModel)DataContext).SetHotkey(mods, vk);
    }
}
