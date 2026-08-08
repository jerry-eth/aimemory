using System.Windows.Controls;
using AiMemoryManager.Services;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Views;

public partial class SmartAnalysisPage : Page
{
    // NavigationView 切换页面时会重建 Page；VM 放在页面静态缓存中，保证分析结果和进行中的任务不丢失。
    private static readonly SmartAnalysisViewModel SharedViewModel = SmartAnalysisViewModel.Instance;

    public SmartAnalysisPage()
    {
        InitializeComponent();
        DataContext = SharedViewModel;
    }
}
