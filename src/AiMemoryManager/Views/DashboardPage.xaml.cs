using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiMemoryManager.Services;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Views;

public partial class DashboardPage : Page
{
    private DashboardViewModel? _vm;

    public DashboardPage()
    {
        InitializeComponent();
        _vm = DataContext as DashboardViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        Unloaded += OnUnloaded;
        UpdateMiniChart();
        UpdateTimelineLabels();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.Recent)
            || e.PropertyName == nameof(DashboardViewModel.RecentSamples))
        {
            UpdateMiniChart();
            UpdateTimelineLabels();
        }
    }

    /// <summary>把最近 150 个百分比映射为点集:X=索引,Y=100-值(0 在底部)。</summary>
    private void UpdateMiniChart()
    {
        if (MiniChart is null) return;
        var recent = _vm?.Recent;
        if (recent is null || recent.Count == 0)
        {
            MiniChart.Points = null;
            return;
        }

        var points = new PointCollection(recent.Count);
        for (int i = 0; i < recent.Count; i++)
            points.Add(new Point(i, 100 - Math.Clamp(recent[i], 0, 100)));
        MiniChart.Points = points;
    }

    private void UpdateTimelineLabels()
    {
        if (TimelineLabels is null) return;
        TimelineLabels.Children.Clear();
        var samples = _vm?.RecentSamples;
        if (samples is null || samples.Count == 0) return;

        var indexes = new[]
        {
            0,
            samples.Count / 4,
            samples.Count / 2,
            (samples.Count - 1) * 3 / 4,
            samples.Count - 1
        };

        for (int i = 0; i < indexes.Length; i++)
        {
            var sample = samples[indexes[i]];
            var label = new TextBlock
            {
                Text = sample.Time.ToLocalTime().ToString("HH:mm:ss"),
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = i switch
                {
                    0 => TextAlignment.Left,
                    4 => TextAlignment.Right,
                    _ => TextAlignment.Center
                }
            };
            TimelineLabels.Children.Add(label);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
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
