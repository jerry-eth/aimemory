using System.Windows.Controls;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Views;

public partial class CSlimPage : Page
{
    // 页面切换会重新创建 Page，复用同一个 VM 以保留扫描/LLM 分析结果和进行中的状态。
    private static readonly CSlimViewModel SharedViewModel = new();

    public CSlimPage()
    {
        InitializeComponent();
        DataContext = SharedViewModel;
    }
}
