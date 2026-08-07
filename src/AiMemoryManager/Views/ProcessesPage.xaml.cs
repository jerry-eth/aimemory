using System.Windows.Controls;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Views;

public partial class ProcessesPage : Page
{
    public ProcessesPage()
    {
        InitializeComponent();
        // 页面每次导航重建;Loaded 时刷新一次进程列表
        Loaded += (_, _) =>
        {
            if (DataContext is ProcessesViewModel vm && vm.RefreshCommand.CanExecute(null))
                vm.RefreshCommand.Execute(null);
        };
    }
}
