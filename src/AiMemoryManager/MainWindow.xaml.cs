using System.ComponentModel;
using System.Drawing;
using System.Windows;
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
        var r = await Locator.Clean.RunL1Async(CleanTrigger.Tray);
        Tray.ShowNotification(Locator.L10n["App.Title"],
            string.Format(Locator.L10n["Clean.Done"], r.FreedBytes / (1 << 20)),
            H.NotifyIcon.Core.NotificationIcon.Info);
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose) { e.Cancel = true; Hide(); }   // 关闭即最小化到托盘,"退出"才真正退出
        else
        {
            TrayIconRenderer.Destroy(_currentTrayIcon);
            _currentTrayIcon = null;
            Tray.Dispose();
        }
        base.OnClosing(e);
    }
}
