using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Views;

namespace AiMemoryManager.ViewModels;

/// <summary>建议卡片 VM:ActionText 走本地化,Risk(low/medium/high)交给 XAML DataTrigger 上色。</summary>
public partial class SuggestionItemViewModel : ObservableObject
{
    public required AnalysisSuggestion Suggestion { get; init; }
    public string ProcessName => Suggestion.ProcessName;
    public string ActionText => Locator.L10n["Analysis.Action." + Suggestion.Action];
    public string Reason => Suggestion.Reason;
    public string Risk => Suggestion.Risk;
    /// <summary>compress 与 terminate 建议均可一键执行(terminate 走 L3 确认流)。</summary>
    public bool CanExecute => Suggestion.Action is "compress" or "terminate";
    public bool IsCompress => Suggestion.Action == "compress";
    public bool IsTerminate => Suggestion.Action == "terminate";

    /// <summary>低/中风险建议默认选中，高风险结束进程必须由用户主动勾选。</summary>
    [ObservableProperty]
    private bool _isSelected;

    public bool IsDefaultSelected => CanExecute && !string.Equals(Risk, "high", StringComparison.OrdinalIgnoreCase);
}
/// <summary>泄漏告警显示项:把 GrowthBytes 预格式化为 MB,时间为本地时间。</summary>
public sealed record LeakAlertItem(string ProcessName, long GrowthMb, string TimeText);

/// <summary>
/// 智能分析页 VM。AnalysisCompleted 在线程池线程触发(调度器自动触发时页面也可能开着),
/// 订阅处必须 Dispatcher 封送;页面切换时由页面级共享实例继续接收结果。
/// </summary>
public partial class SmartAnalysisViewModel : ObservableObject, IDisposable
{
    public static SmartAnalysisViewModel Instance { get; } = new();
    public AnalysisResult? LastResult { get; private set; }
    [ObservableProperty] private AnalysisReport? _report;
    [ObservableProperty] private bool _hasReport;
    public ObservableCollection<SuggestionItemViewModel> Suggestions { get; } = new();
    public ObservableCollection<LeakAlertItem> LeakAlerts { get; } = new();
    public ObservableCollection<AnalysisChatMessage> ChatMessages { get; } = new();

    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private bool _hasLeakAlerts;
    [ObservableProperty] private bool _hasSuggestions;
    [ObservableProperty] private bool _hasExecutableSuggestions;
    [ObservableProperty] private bool _hasSelectedSuggestions;
    [ObservableProperty] private int _selectedSuggestionCount;
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private bool _isBatchBusy;
    [ObservableProperty] private string _chatInput = "";
    [ObservableProperty] private bool _isChatBusy;
    [ObservableProperty] private AnalysisActionPlan? _pendingChatPlan;
    [ObservableProperty] private bool _hasPendingChatPlan;
    [ObservableProperty] private string _chatPlanText = "";

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
        LastResult = r;
        Report = AnalysisReportBuilder.Build(r, Locator.Native.GetSystemMemory(), Locator.Native.GetProcessSnapshots().Count);
        HasReport = true;
        foreach (var oldItem in Suggestions)
            oldItem.PropertyChanged -= OnSuggestionPropertyChanged;
        Suggestions.Clear();
        foreach (var s in r.Suggestions)
        {
            var item = new SuggestionItemViewModel { Suggestion = s };
            item.IsSelected = item.IsDefaultSelected;
            item.PropertyChanged += OnSuggestionPropertyChanged;
            Suggestions.Add(item);
        }
        HasSuggestions = Suggestions.Count > 0;
        UpdateSelectionState();
        UsageText = string.Format(Locator.L10n["Analysis.Usage"], r.Usage.InputTokens, r.Usage.OutputTokens)
                  + (r.FromCache ? " " + Locator.L10n["Analysis.FromCache"] : "");
        StatusText = string.Format(Locator.L10n["Analysis.LastRun"], r.Time.ToLocalTime().ToString("HH:mm:ss"));
        RefreshLeakAlerts();
        AddChatMessage("assistant", $"{Report.Summary} {string.Join("；", Report.Recommendations)}".Trim());
    }

    private void OnSuggestionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SuggestionItemViewModel.IsSelected))
            UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        HasExecutableSuggestions = Suggestions.Any(x => x.CanExecute);
        SelectedSuggestionCount = Suggestions.Count(x => x.CanExecute && x.IsSelected);
        HasSelectedSuggestions = SelectedSuggestionCount > 0;
        SelectionSummary = string.Format(Locator.L10n["Analysis.SelectedCount"], SelectedSuggestionCount, Suggestions.Count(x => x.CanExecute));
    }

    [RelayCommand]
    private void SelectAllSuggestions()
    {
        foreach (var item in Suggestions.Where(x => x.CanExecute))
            item.IsSelected = true;
        UpdateSelectionState();
    }

    [RelayCommand]
    private void ClearSuggestionSelection()
    {
        foreach (var item in Suggestions)
            item.IsSelected = false;
        UpdateSelectionState();
    }

    /// <summary>
    /// 执行用户勾选的分析建议：压缩只作用于勾选的进程，结束进程仍经过统一确认和 L3 明细确认。
    /// LLM 只提供建议，所有真正的动作都映射到现有安全服务，不执行任意命令或路径。
    /// </summary>
    [RelayCommand]
    private async Task ExecuteSelectedSuggestionsAsync()
    {
        if (IsBatchBusy) return;
        var selected = Suggestions.Where(x => x.CanExecute && x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = Locator.L10n["Analysis.BatchNoSelection"];
            return;
        }

        try
        {
            var snapshots = Locator.Native.GetProcessSnapshots();
            var compressNames = selected.Where(x => x.IsCompress)
                .Select(x => NormalizeProcessName(x.ProcessName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var terminateNames = selected.Where(x => x.IsTerminate)
                .Select(x => NormalizeProcessName(x.ProcessName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var compressTargets = snapshots
                .Where(p => compressNames.Contains(NormalizeProcessName(p.Name)))
                .Where(p => p.WorkingSetBytes > 20L << 20)
                .Where(p => !Locator.Whitelist.IsExcluded(p.Name))
                .Where(p => !Locator.Whitelist.IsSystemCritical(p.Name))
                .Where(p => !Locator.Guard.IsProtected(p.Pid))
                .ToList();
            var terminateCandidates = snapshots
                .Where(p => terminateNames.Contains(NormalizeProcessName(p.Name)))
                .ToList();
            var killable = Locator.Terminator.FilterCandidates(terminateCandidates.Select(p => p.Pid).ToList()).ToHashSet();
            var terminateTargets = terminateCandidates.Where(p => killable.Contains(p.Pid)).ToList();

            if (compressTargets.Count == 0 && terminateTargets.Count == 0)
            {
                StatusText = Locator.L10n["Analysis.BatchNoTargets"];
                AddChatMessage("assistant", Locator.L10n["Analysis.BatchNoTargets"]);
                return;
            }

            var summary = string.Format(Locator.L10n["Analysis.BatchConfirm"],
                compressTargets.Count, terminateTargets.Count);
            var confirm = new SlimConfirmDialog(
                Locator.L10n["Analysis.BatchTitle"], summary, Locator.L10n["Analysis.BatchExecute"])
            { Owner = System.Windows.Application.Current.MainWindow };
            if (confirm.ShowDialog() != true) return;

            IReadOnlyCollection<int> confirmedPids = Array.Empty<int>();
            if (terminateTargets.Count > 0)
            {
                var rows = terminateTargets.Select(t => new TerminateConfirmItem(
                    t.Pid, t.Name, t.Path, t.WorkingSetBytes,
                    Locator.Unsaved.HasUnsavedSigns(t.Pid))).ToList();
                var terminateDialog = new TerminateConfirmDialog(rows)
                { Owner = System.Windows.Application.Current.MainWindow };
                if (terminateDialog.ShowDialog() != true) return;
                confirmedPids = terminateDialog.SelectedPids;
            }

            IsBatchBusy = true;
            StatusText = Locator.L10n["Analysis.BatchProcessing"];
            CleanResult? compressed = null;
            TerminateResult? terminated = null;
            if (compressTargets.Count > 0)
                compressed = await Locator.Clean.RunL1Async(CleanTrigger.Analysis,
                    compressTargets.Select(p => p.Pid).ToList());
            if (confirmedPids.Count > 0)
                terminated = await Locator.Terminator.TerminateAsync(confirmedPids);

            var compressedCount = compressed?.ProcessCount ?? 0;
            var terminatedOk = terminated?.Items.Count(x => x.Success) ?? 0;
            var terminatedFailed = terminated?.Items.Count(x => !x.Success) ?? 0;
            var freedBytes = (compressed?.FreedBytes ?? 0) + (terminated?.FreedBytes ?? 0);
            if (terminatedOk > 0)
                Locator.History.Record(new CleanHistoryEntry(DateTimeOffset.Now, CleanLevel.L3,
                    terminated!.FreedBytes, terminatedOk, CleanTrigger.Analysis));

            var resultText = string.Format(Locator.L10n["Analysis.BatchExecuted"],
                freedBytes / (1 << 20), compressedCount, terminatedOk, terminatedFailed);
            StatusText = resultText;
            AddChatMessage("assistant", resultText);

            var terminatedNames = terminated?.Items.Where(x => x.Success)
                .Select(x => NormalizeProcessName(x.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in selected.ToList())
            {
                var handled = item.IsCompress
                    ? compressTargets.Any(p => string.Equals(NormalizeProcessName(p.Name), NormalizeProcessName(item.ProcessName), StringComparison.OrdinalIgnoreCase))
                    : terminatedNames.Contains(NormalizeProcessName(item.ProcessName));
                if (handled)
                {
                    item.PropertyChanged -= OnSuggestionPropertyChanged;
                    Suggestions.Remove(item);
                }
            }
            HasSuggestions = Suggestions.Count > 0;
            UpdateSelectionState();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            AddChatMessage("assistant", string.Format(Locator.L10n["Analysis.BatchFailed"], ex.Message));
        }
        finally { IsBatchBusy = false; }
    }

    private static string NormalizeProcessName(string name)
    {
        var value = name.Trim();
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
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
    private async Task SendChatAsync()
    {
        if (IsChatBusy || string.IsNullOrWhiteSpace(ChatInput)) return;
        var text = ChatInput.Trim();
        ChatInput = "";
        ClearChatPlan();
        var history = ChatMessages.ToList();
        AddChatMessage("user", text);
        IsChatBusy = true;
        try
        {
            var response = await Locator.AnalysisChat.ChatAsync(Report, history, text);
            AddChatMessage("assistant", response.Answer);
            PendingChatPlan = response.Plan is { IsExecutable: true } ? response.Plan : null;
            HasPendingChatPlan = PendingChatPlan is not null;
            ChatPlanText = PendingChatPlan is null ? "" : DescribePlan(PendingChatPlan);
        }
        catch (Exception ex)
        {
            AddChatMessage("assistant", ex.Message);
        }
        finally { IsChatBusy = false; }
    }

    [RelayCommand]
    private async Task ExecuteChatPlanAsync()
    {
        if (PendingChatPlan is not { IsExecutable: true } plan) return;
        try
        {
            IReadOnlyCollection<int> pids = Array.Empty<int>();
            if (plan.Operation == "terminate_processes")
            {
                var targets = Locator.AnalysisActions.ResolveTerminateTargets(plan);
                if (targets.Count == 0)
                {
                    AddChatMessage("assistant", Locator.L10n["Analysis.NoneKillable"]);
                    ClearChatPlan();
                    return;
                }
                var rows = targets.Select(t => new TerminateConfirmItem(t.Pid, t.Name, t.Path,
                    t.WorkingSetBytes, Locator.Unsaved.HasUnsavedSigns(t.Pid))).ToList();
                var dialog = new TerminateConfirmDialog(rows) { Owner = System.Windows.Application.Current.MainWindow };
                if (dialog.ShowDialog() != true) return;
                pids = dialog.SelectedPids;
            }
            else
            {
                var dialog = new SlimConfirmDialog(
                    Locator.L10n["Analysis.ChatTitle"],
                    DescribePlan(plan) + "\n\n" + Locator.L10n["Analysis.ChatConfirm"],
                    Locator.L10n["Analysis.ChatExecute"])
                { Owner = System.Windows.Application.Current.MainWindow };
                if (dialog.ShowDialog() != true) return;
            }

            var result = await Locator.AnalysisActions.ExecuteAsync(plan, pids);
            AddChatMessage("assistant", string.Format(Locator.L10n["Analysis.ChatExecuted"],
                result.FreedBytes / (1 << 20), result.ProcessCount, result.FailedCount));
            ClearChatPlan();
        }
        catch (Exception ex) { AddChatMessage("assistant", ex.Message); }
    }

    private void AddChatMessage(string role, string content) =>
        ChatMessages.Add(new AnalysisChatMessage(role, content, DateTimeOffset.Now));

    private void ClearChatPlan()
    {
        PendingChatPlan = null;
        HasPendingChatPlan = false;
        ChatPlanText = "";
    }

    private static string DescribePlan(AnalysisActionPlan plan)
    {
        var operation = plan.Operation switch
        {
            "clean_working_sets" => "清理进程工作集",
            "purge_standby" => "清理待机列表",
            "terminate_processes" => "结束指定进程",
            _ => "无操作"
        };
        var targets = plan.Targets.Count == 0 ? "全部可清理目标" : string.Join(", ", plan.Targets);
        return $"{operation}：{targets}\n理由：{plan.Reason}\n风险：{plan.Risk}";
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
        if (item is null) return;
        try
        {
            var targets = Locator.Native.GetProcessSnapshots()
                .Where(p => string.Equals(NormalizeProcessName(p.Name), NormalizeProcessName(item.ProcessName), StringComparison.OrdinalIgnoreCase))
                .Where(p => p.WorkingSetBytes > 20L << 20)
                .Where(p => !Locator.Whitelist.IsExcluded(p.Name))
                .Where(p => !Locator.Whitelist.IsSystemCritical(p.Name))
                .Where(p => !Locator.Guard.IsProtected(p.Pid))
                .Select(p => p.Pid)
                .ToList();
            if (targets.Count == 0)
            {
                StatusText = Locator.L10n["Analysis.BatchNoTargets"];
                return;
            }
            var r = await Locator.Clean.RunL1Async(CleanTrigger.Analysis, targets);
            var resultText = string.Format(Locator.L10n["Analysis.Compressed"], r.FreedBytes / (1 << 20));
            StatusText = resultText;
            AddChatMessage("assistant", $"{item.ProcessName}：{resultText}");
            item.PropertyChanged -= OnSuggestionPropertyChanged;
            Suggestions.Remove(item);
            HasSuggestions = Suggestions.Count > 0;
            UpdateSelectionState();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Locator.L10n["Dashboard.L1Failed"], ex.Message);
        }
    }
    /// <summary>
    /// terminate 建议一键执行:与进程页 L3 同一强制确认流(FilterCandidates → 确认对话框 → TerminateAsync)。
    /// 同名多实例:按进程名从全新快照解析 pid,所有匹配实例一起进清单;历史以 CleanTrigger.Analysis 记一次。
    /// </summary>
    [RelayCommand]
    private async Task TerminateAsync(SuggestionItemViewModel? item)
    {
        if (item is null) return;
        // async 命令:全程 try/catch,失败只落状态文本
        try
        {
            var snaps = Locator.Native.GetProcessSnapshots();
            var sameName = snaps
                .Where(p => string.Equals(NormalizeProcessName(p.Name), NormalizeProcessName(item.ProcessName), StringComparison.OrdinalIgnoreCase))
                .ToList();
            var killable = Locator.Terminator
                .FilterCandidates(sameName.Select(p => p.Pid).ToList()).ToHashSet();
            var targets = sameName.Where(p => killable.Contains(p.Pid)).ToList();
            // 建议已被 AnalysisService 预过滤,但快照可能已变化/进程被加白,需重查
            if (targets.Count == 0)
            {
                StatusText = Locator.L10n["Analysis.NoneKillable"];
                return;
            }

            var rows = targets.Select(t => new TerminateConfirmItem(
                t.Pid, t.Name, t.Path, t.WorkingSetBytes,
                Locator.Unsaved.HasUnsavedSigns(t.Pid))).ToList();

            // L3 默认必须用户确认:无静默终止路径
            var dlg = new TerminateConfirmDialog(rows) { Owner = System.Windows.Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            var r = await Locator.Terminator.TerminateAsync(dlg.SelectedPids);
            int ok = r.Items.Count(i => i.Success);
            int fail = r.Items.Count - ok;
            // 成功终止后才记历史(Analysis 触发 L3 的唯一记录点)
            if (ok > 0)
                Locator.History.Record(new CleanHistoryEntry(
                    DateTimeOffset.Now, CleanLevel.L3, r.FreedBytes, ok, CleanTrigger.Analysis));
            StatusText = string.Format(Locator.L10n["L3.Result"], ok, r.FreedBytes / (1 << 20), fail);
            // 该卡标记已处理:从建议列表移除
            item.PropertyChanged -= OnSuggestionPropertyChanged;
            Suggestions.Remove(item);
            HasSuggestions = Suggestions.Count > 0;
            UpdateSelectionState();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
