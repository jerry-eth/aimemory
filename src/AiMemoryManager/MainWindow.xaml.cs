using AiMemoryManager.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AiMemoryManager;

/// <summary>
/// 主窗口:FluentWindow + Mica + NavigationView。
/// WPF-UI 4.x 的 NavigationView 内置 Frame,选中项按 TargetPageType 自动导航。
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            ApplicationThemeManager.ApplySystemTheme();     // 主题跟随系统
            RootNavigation.Navigate(typeof(DashboardPage)); // 初始页
        };
    }
}
