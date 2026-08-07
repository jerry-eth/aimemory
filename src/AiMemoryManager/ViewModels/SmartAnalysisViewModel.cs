using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

/// <summary>建议卡片 VM:ActionText 走本地化,Risk(low/medium/high)交给 XAML DataTrigger 上色。</summary>
public partial class SuggestionItemViewModel : ObservableObject
{
    public required AnalysisSuggestion Suggestion { get; init; }
    public string ProcessName => Suggestion.ProcessName;
    public string ActionText => Locator.L10n["Analysis.Action." + Suggestion.Action];
    public string Reason => Suggestion.Reason;
    public string Risk => Suggestion.Risk;
    public bool CanExecute => Suggestion.Action == "compress";
    public bool IsTerminate => Suggestion.Action == "terminate";
}

/// <summary>泄漏告警显示项:把 GrowthBytes 预格式化为 MB,时间为本地时间。</summary>
public sealed record LeakAlertItem(string ProcessName, long GrowthMb, string TimeText);

/// <summary>
/// 智能分析页 VM。AnalysisCompleted 在线程池线程触发(调度器自动触发时页面也可能开着),
/// 订阅处必须 Dispatcher 封送;页面 Unloaded 时 Dispose 退订,避免泄漏(与 DashboardViewModel 一致)。
/// </summary>
public partial class SmartAnalysisViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<SuggestionItemViewModel> Suggestions { get; } = new();
    public ObservableCollection<LeakAlertItem> LeakAlerts { get; } = new();

    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private bool _hasLeakAlerts;
    [ObservableProperty] private bool _hasSuggestions;

    public SmartAnalysisViewModel()
    {
        HasProfile = Locator.Profiles.GetActive() is not null;
        RefreshLeakAlerts();
        Locator.Analysis.AnalysisCompleted += OnAnalysisCompleted;
    }

    private void OnAnalysisCompleted(object? s, AnalysisResult r) =>
        App.Current.Dispatcher.BeginInvoke(() => ApplyResult(r));

    /// <summary>手动与自动触发的分析结果统一从这里进 UI(AnalyzeAsync 内部先触发事件再返回,单一填充路径)。</summary>
    private void ApplyResult(AnalysisResult r)
    {
        Suggestions.Clear();
        foreach (var s in r.Suggestions)
            Suggestions.Add(new SuggestionItemViewModel { Suggestion = s });
        HasSuggestions = Suggestions.Count > 0;
        UsageText = string.Format(Locator.L10n["Analysis.Usage"], r.Usage.InputTokens, r.Usage.OutputTokens)
                  + (r.FromCache ? " " + Locator.L10n["Analysis.FromCache"] : "");
        StatusText = string.Format(Locator.L10n["Analysis.LastRun"], r.Time.ToLocalTime().ToString("HH:mm:ss"));
        RefreshLeakAlerts();
    }

    /// <summary>手动触发前置检查:档案存在 + 未达月度预算(超预算手动分析被拦并给出可见提示)。</summary>
    private bool PrecheckManual()
    {
        HasProfile = Locator.Profiles.GetActive() is not null;
        if (!HasProfile) { StatusText = Locator.L10n["Analysis.NoProfile"]; return false; }
        var budget = Locator.Settings.Current.MonthlyTokenBudget;
        if (budget > 0 && !Locator.TokenStats.IsAutoTriggerAllowed(budget))
        {
            StatusText = Locator.L10n["Tokens.BudgetHit"];
            return false;
        }
        return true;
    }

    [RelayCommand]
    private Task RunAnalysisAsync() => RunAsync(forceRefresh: false);

    [RelayCommand]
    private Task ForceRefreshAsync() => RunAsync(forceRefresh: true);

    private async Task RunAsync(bool forceRefresh)
    {
        if (!PrecheckManual()) return;
        IsAnalyzing = true;
        StatusText = Locator.L10n["Analysis.Running"];
        try { await Locator.Analysis.AnalyzeAsync(AnalysisTrigger.Manual, forceRefresh); }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsAnalyzing = false; }
    }

    /// <summary>泄漏告警行内"智能分析":以 Leak 触发器发起分析,结果走既有 AnalysisCompleted 填充路径。</summary>
    [RelayCommand]
    private async Task AnalyzeLeakAsync()
    {
        if (!PrecheckManual()) return;
        IsAnalyzing = true;
        StatusText = Locator.L10n["Analysis.Running"];
        try { await Locator.Analysis.AnalyzeAsync(AnalysisTrigger.Leak); }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsAnalyzing = false; }
    }

    [RelayCommand]
    private async Task CompressAsync(SuggestionItemViewModel? item)
    {
        // M2 决策:不新增单进程压缩 API(YAGNI),"立即压缩"执行全量 L1(语义=压缩所有非豁免进程)
        try
        {
            var r = await Locator.Clean.RunL1Async(CleanTrigger.Manual);
            StatusText = string.Format(Locator.L10n["Analysis.Compressed"], r.FreedBytes / (1 << 20));
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Locator.L10n["Dashboard.L1Failed"], ex.Message);
        }
    }

    private void RefreshLeakAlerts()
    {
        LeakAlerts.Clear();
        foreach (var a in Locator.LeakDetection.RecentAlerts)
            LeakAlerts.Add(new LeakAlertItem(a.ProcessName, a.GrowthBytes >> 20,
                a.Time.ToLocalTime().ToString("MM-dd HH:mm")));
        HasLeakAlerts = LeakAlerts.Count > 0;
    }

    public void Dispose() => Locator.Analysis.AnalysisCompleted -= OnAnalysisCompleted;
}
