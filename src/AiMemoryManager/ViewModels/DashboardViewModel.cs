using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

/// <summary>
/// 仪表盘页 VM:订阅监控采样与清理完成事件(两者均发生在线程池线程,需 Dispatcher 封送)。
/// NavigationView 每次导航都会重建页面/VM,页面 Unloaded 时须 Dispose 退订,避免泄漏。
/// </summary>
public partial class DashboardViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _usedText = "-";
    [ObservableProperty] private string _availableText = "-";
    [ObservableProperty] private string _totalText = "-";
    [ObservableProperty] private string _lastCleanText = "";
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private List<double> _recent = new();

    public DashboardViewModel()
    {
        Locator.Monitor.Sampled += OnSampled;
        Locator.Clean.CleanCompleted += OnCleaned;
        Refresh();
    }

    private void OnSampled(object? s, SystemMemoryInfo info) =>
        App.Current.Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        var m = Locator.Monitor;
        var info = Locator.Native.GetSystemMemory();
        Percent = info.UsedPercent;
        TotalText = $"{info.TotalBytes / (1 << 30)} GB";
        AvailableText = $"{info.AvailableBytes / (1 << 30)} GB";
        UsedText = $"{(info.TotalBytes - info.AvailableBytes) / (1 << 30)} GB";
        Recent = m.RecentPercents.ToList();
    }

    private void OnCleaned(object? s, CleanResult r) =>
        App.Current.Dispatcher.Invoke(() =>
            LastCleanText = string.Format(Locator.L10n["Dashboard.LastClean"], r.FreedBytes / (1 << 20)));

    [RelayCommand]
    private async Task CleanL1Async()
    {
        IsCleaning = true;
        try { await Locator.Clean.RunL1Async(CleanTrigger.Manual); Refresh(); }
        catch (Exception ex)
        {
            // 与 L2 一致:清理失败不能静默,给本地化失败文案;M3 换 InfoBar
            LastCleanText = string.Format(Locator.L10n["Dashboard.L1Failed"], ex.Message);
        }
        finally { IsCleaning = false; }
    }

    [RelayCommand]
    private async Task CleanL2Async()
    {
        IsCleaning = true;
        try { await Locator.Clean.RunL2Async(CleanTrigger.Manual); Refresh(); }
        catch (Exception ex)
        {
            // L2 依赖提权助手(Task 16 才进输出目录),失败时给友好文案;M3 换 InfoBar
            LastCleanText = string.Format(Locator.L10n["Dashboard.L2Failed"], ex.Message);
        }
        finally { IsCleaning = false; }
    }

    public void Dispose()
    {
        Locator.Monitor.Sampled -= OnSampled;
        Locator.Clean.CleanCompleted -= OnCleaned;
    }
}
