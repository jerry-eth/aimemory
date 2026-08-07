using System.Windows.Controls;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Views;

public partial class SmartAnalysisPage : Page
{
    private SmartAnalysisViewModel? _vm;

    public SmartAnalysisPage()
    {
        InitializeComponent();
        _vm = DataContext as SmartAnalysisViewModel;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // NavigationView 每次导航重建页面;退订 AnalysisCompleted,防止事件订阅泄漏(同 DashboardPage)
        _vm?.Dispose();
        _vm = null;
        Unloaded -= OnUnloaded;
    }
}
