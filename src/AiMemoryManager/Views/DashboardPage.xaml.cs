using System.ComponentModel;
using System.Windows.Media;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Views;

public partial class DashboardPage : System.Windows.Controls.Page
{
    private DashboardViewModel? _vm;

    public DashboardPage()
    {
        InitializeComponent();
        _vm = DataContext as DashboardViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        Unloaded += OnUnloaded;
        UpdateMiniChart();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.Recent)) UpdateMiniChart();
    }

    /// <summary>把最近 150 个百分比映射为点集:X=索引,Y=100-值(0 在底部)。Stretch=Fill 负责缩放到控件。</summary>
    private void UpdateMiniChart()
    {
        if (FindName("MiniChart") is not System.Windows.Shapes.Polyline chart) return;
        var recent = _vm?.Recent;
        if (recent is null || recent.Count == 0) { chart.Points = null; return; }
        var points = new PointCollection(recent.Count);
        for (int i = 0; i < recent.Count; i++)
            points.Add(new System.Windows.Point(i, 100 - Math.Clamp(recent[i], 0, 100)));
        chart.Points = points;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // NavigationView 每次导航重建页面;退订并释放 VM,防止事件订阅泄漏
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Dispose();
            _vm = null;
        }
        Unloaded -= OnUnloaded;
    }
}
