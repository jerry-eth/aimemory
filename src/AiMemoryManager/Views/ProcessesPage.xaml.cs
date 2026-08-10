using System.Windows.Controls;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Views;

public partial class ProcessesPage : Page
{
    public ProcessesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ProcessesViewModel vm)
            vm.StartMonitoring();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ProcessesViewModel vm)
            vm.StopMonitoring();
    }
}
