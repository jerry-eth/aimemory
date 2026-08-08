using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

/// <summary>
/// 设置页 VM:语言切换即时生效(L10n 触发 Item[] 通知,全部绑定刷新),所有变更立即保存。
/// 页面每次导航重建,构造时读取当前值,无陈旧状态。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>语言下拉框选中索引:0=跟随系统,1=中文,2=English。</summary>
    public int LanguageIndex
    {
        get => Locator.Settings.Current.Language switch
        {
            "zh-CN" => 1,
            "en" => 2,
            _ => 0
        };
        set
        {
            var lang = value switch
            {
                1 => "zh-CN",
                2 => "en",
                _ => "auto"
            };
            if (Locator.Settings.Current.Language == lang) return;
            Locator.Settings.Current.Language = lang;
            // 即时生效:CurrentLanguage setter 内部触发 PropertyChanged("Item[]"),所有绑定自动刷新
            if (lang == "auto") Locator.L10n.SetAuto();
            else Locator.L10n.CurrentLanguage = lang;
            Locator.Settings.Save();
            OnPropertyChanged();
        }
    }

    public bool Animations
    {
        get => Locator.Settings.Current.AnimationsEnabled;
        set
        {
            if (Locator.Settings.Current.AnimationsEnabled == value) return;
            Locator.Settings.Current.AnimationsEnabled = value;
            Locator.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>FR-8.3 开机自启:setter 写注册表 Run 键并保存设置,getter 回读注册表真实状态。</summary>
    public bool AutoStartEnabled
    {
        get => Locator.Startup.IsEnabled;
        set
        {
            if (Locator.Startup.IsEnabled == value) return;
            Locator.Startup.SetEnabled(value);   // 内部已写设置并 Save
            OnPropertyChanged();
        }
    }

    /// <summary>FR-8.4 应用内通知总闸,即时存储。</summary>
    public bool NotificationsEnabled
    {
        get => Locator.Settings.Current.NotificationsEnabled;
        set
        {
            if (Locator.Settings.Current.NotificationsEnabled == value) return;
            Locator.Settings.Current.NotificationsEnabled = value;
            Locator.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>FR-8.5 当前热键显示文本,如 "Ctrl+Shift+M"。</summary>
    public string HotkeyText => HotkeyService.Format(
        Locator.Settings.Current.HotkeyModifiers, Locator.Settings.Current.HotkeyKey);

    [ObservableProperty]
    private bool _hotkeyFailed;   // 热键被占用时设置页提示

    /// <summary>FR-8.5 改键:先尝试注册新组合,成功才持久化;失败(被占用)回滚注册旧组合、不写设置,置 HotkeyFailed 降级提示。</summary>
    public void SetHotkey(int modifiers, int key)
    {
        int oldModifiers = Locator.Settings.Current.HotkeyModifiers;
        int oldKey = Locator.Settings.Current.HotkeyKey;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle;
        if (hwnd != IntPtr.Zero && !Locator.Hotkey.Register(hwnd, modifiers, key))
        {
            // 新组合被占用:HotkeyService.Register 内部已先注销旧组合,必须立即回滚重注册,
            // 否则旧热键死掉且死组合被持久化,功能静默丢失
            Locator.Hotkey.Register(hwnd, oldModifiers, oldKey);
            HotkeyFailed = true;
            OnPropertyChanged(nameof(HotkeyText));
            return;
        }
        // 注册成功(或窗口句柄未就绪,注册将在下次启动时按设置执行)才持久化
        Locator.Settings.Current.HotkeyModifiers = modifiers;
        Locator.Settings.Current.HotkeyKey = key;
        Locator.Settings.Save();
        HotkeyFailed = false;
        OnPropertyChanged(nameof(HotkeyText));
    }

    public string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
