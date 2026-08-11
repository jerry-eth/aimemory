using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

/// <summary>清理历史卡一行:级别与触发方式经 i18n 转为本地化文本。</summary>
public record HistoryRow(string TimeText, string LevelText, string FreedText, string TriggerText);

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
    [ObservableProperty] private List<MemorySample> _recentSamples = new();

    /// <summary>清理历史卡(最新 10 条)。History.Changed 可能在线程池线程触发,须 Dispatcher 封送。</summary>
    public ObservableCollection<HistoryRow> HistoryRows { get; } = new();
    public bool HasHistory => HistoryRows.Count > 0;
    public bool HistoryEmpty => HistoryRows.Count == 0;
    public bool AnimationsEnabled => Locator.Settings.Current.AnimationsEnabled;
    public bool IsL2Available => Locator.L2.IsAvailable;

    public DashboardViewModel()
    {
        Locator.Monitor.Sampled += OnSampled;
        Locator.Clean.CleanCompleted += OnCleaned;
        Locator.History.Changed += OnHistoryChanged;
        Locator.Settings.SettingsSaved += OnSettingsSaved;
        RebuildHistory();
        Refresh();
    }

    private void OnHistoryChanged(object? s, EventArgs e) =>
        App.Current.Dispatcher.Invoke(RebuildHistory);

    private void OnSettingsSaved(object? s, EventArgs e) =>
        App.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(AnimationsEnabled)));

    private void RebuildHistory()
    {
        HistoryRows.Clear();
        foreach (var e in Locator.History.Entries.Take(10))
            HistoryRows.Add(new HistoryRow(
                e.Time.ToString("MM-dd HH:mm"),
                Locator.L10n[$"Dash.Level.{e.Level}"],
                $"{e.FreedBytes / (1 << 20)} MB",
                Locator.L10n[$"Dash.Trigger.{e.Trigger}"]));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryEmpty));
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
        RecentSamples = m.RecentSamples.ToList();
        Recent = RecentSamples.Select(s => s.UsedPercent).ToList();
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
        Locator.History.Changed -= OnHistoryChanged;
        Locator.Settings.SettingsSaved -= OnSettingsSaved;
    }
}
