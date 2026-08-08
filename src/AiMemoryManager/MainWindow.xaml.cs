using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AiMemoryManager;

/// <summary>
/// 主窗口:FluentWindow + Mica + NavigationView。
/// WPF-UI 4.x 的 NavigationView 内置 Frame,选中项按 TargetPageType 自动导航。
/// 托盘:动态百分比图标(绿/橙/红)+ 右键菜单(清理/打开/退出)+ 关闭最小化到托盘。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private bool _reallyClose;
    private Icon? _currentTrayIcon;

    public MainWindow()
    {
        InitializeComponent();
        PreviewMouseWheel += OnPreviewMouseWheel;

        Loaded += (_, _) =>
        {
            ApplicationThemeManager.ApplySystemTheme();     // 主题跟随系统
            RootNavigation.Navigate(typeof(DashboardPage)); // 初始页
            // 首次采样可能早于订阅,用最近一次采样(或 0)先渲染一次托盘图标
            UpdateTray(Locator.Monitor.RecentPercents.LastOrDefault());
        };

        // Sampled 在线程池线程触发,先切回 UI 线程再更新托盘
        Locator.Monitor.Sampled += (_, info) =>
            Dispatcher.BeginInvoke(() => UpdateTray(info.UsedPercent));

        // 泄漏告警(App 转发,线程不定)→ 切回 UI 线程弹 Windows 通知
        App.BlacklistActioned += result => Dispatcher.BeginInvoke(() =>
        {
            if (!Locator.Settings.Current.NotificationsEnabled) return;
            var key = result.Status == "terminated" ? "Blacklist.NotifyTerminated" : "Blacklist.NotifySkipped";
            Tray.ShowNotification(Locator.L10n["App.Title"],
                string.Format(Locator.L10n[key], result.ProcessName, result.Reason),
                result.Status == "terminated" ? H.NotifyIcon.Core.NotificationIcon.Warning : H.NotifyIcon.Core.NotificationIcon.Info);
        });

        App.LeakAlerted += alert => Dispatcher.BeginInvoke(() =>
        {
            // FR-8.4 通知总闸:关闭后应用内不再弹任何通知
            if (!Locator.Settings.Current.NotificationsEnabled) return;
            Tray.ShowNotification(Locator.L10n["App.Title"],
                string.Format(Locator.L10n["Leak.Alert"], alert.ProcessName, alert.GrowthBytes / (1 << 20)),
                H.NotifyIcon.Core.NotificationIcon.Warning);
        });

        // FR-8.5 全局热键:WndProc 钩子已运行在 UI 线程,仍经 Dispatcher 封送与托盘清理保持同一模式
        Locator.Hotkey.Pressed += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            // async void 处理器:必须自吞异常,清理失败不能拖垮进程
            try
            {
                var r = await Locator.Clean.RunL1Async(CleanTrigger.Manual);
                if (Locator.Settings.Current.NotificationsEnabled)   // FR-8.4 通知总闸
                    Tray.ShowNotification(Locator.L10n["App.Title"],
                        string.Format(Locator.L10n["Clean.Done"], r.FreedBytes / (1 << 20)),
                        H.NotifyIcon.Core.NotificationIcon.Info);
            }
            catch (Exception ex)
            {
                if (Locator.Settings.Current.NotificationsEnabled)
                    Tray.ShowNotification(Locator.L10n["App.Title"], ex.Message,
                        H.NotifyIcon.Core.NotificationIcon.Error);
            }
        });
    }

    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // NavigationView 和部分页面控件可能会提前处理滚轮事件。这里从鼠标所在控件
        // 向上寻找滚动容器，确保鼠标停在内容区域时也能滚动页面，而不必精准移到滚动条。
        var scrollAmount = e.Delta / 120.0 * 48.0;
        var current = e.OriginalSource as DependencyObject;

        while (current is not null)
        {
            if (current is ScrollViewer viewer && TryScroll(viewer, scrollAmount))
            {
                e.Handled = true;
                return;
            }

            current = GetParent(current);
        }
    }

    private static bool TryScroll(ScrollViewer viewer, double scrollAmount)
    {
        if (viewer.ScrollableHeight <= 0.5)
            return false;

        var oldOffset = viewer.VerticalOffset;
        var newOffset = Math.Clamp(oldOffset - scrollAmount, 0, viewer.ScrollableHeight);

        // 到达内部列表边缘时不拦截事件，让外层页面滚动容器继续接管滚轮。
        if (Math.Abs(newOffset - oldOffset) < 0.5)
            return false;

        viewer.ScrollToVerticalOffset(newOffset);
        return true;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is FrameworkContentElement contentElement)
            return contentElement.Parent;

        return VisualTreeHelper.GetParent(child);
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // FR-8.5:句柄创建后挂 WndProc 钩子,WM_HOTKEY 由 HotkeyService 分发
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        System.Windows.Interop.HwndSource.FromHwnd(helper.Handle)?.AddHook(Locator.Hotkey.WndProc);
    }

    private void UpdateTray(double percent)
    {
        var newIcon = TrayIconRenderer.Render(percent);
        var oldIcon = _currentTrayIcon;
        _currentTrayIcon = newIcon;
        Tray.Icon = newIcon;
        TrayIconRenderer.Destroy(oldIcon);   // 防 GDI 句柄泄漏
        Tray.ToolTipText = string.Format(Locator.L10n["Tray.ToolTip"], (int)Math.Round(percent));
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnTrayClean(object sender, RoutedEventArgs e)
    {
        // async void 事件处理器:必须自吞异常,否则清理失败会直接崩溃进程
        try
        {
            var r = await Locator.Clean.RunL1Async(CleanTrigger.Tray);
            if (Locator.Settings.Current.NotificationsEnabled)   // FR-8.4 通知总闸
                Tray.ShowNotification(Locator.L10n["App.Title"],
                    string.Format(Locator.L10n["Clean.Done"], r.FreedBytes / (1 << 20)),
                    H.NotifyIcon.Core.NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            if (Locator.Settings.Current.NotificationsEnabled)   // FR-8.4 通知总闸
                Tray.ShowNotification(Locator.L10n["App.Title"], ex.Message,
                    H.NotifyIcon.Core.NotificationIcon.Error);
        }
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose && !App.IsSessionEnding) { e.Cancel = true; Hide(); }   // 关闭即最小化到托盘,"退出"才真正退出;关机/注销时不拦截
        else
        {
            TrayIconRenderer.Destroy(_currentTrayIcon);
            _currentTrayIcon = null;
            Tray.Dispose();
        }
        base.OnClosing(e);
    }
}
