using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Views;

namespace AiMemoryManager.ViewModels;

/// <summary>批量加入进程行，避免首次加载时为每一行触发布局和虚拟化重建。</summary>
public sealed class ProcessItemCollection : ObservableCollection<ProcessItemViewModel>
{
    private bool _suppressNotifications;

    public void AddRange(IEnumerable<ProcessItemViewModel> items)
    {
        CheckReentrancy();
        _suppressNotifications = true;
        try
        {
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnCollectionChanged(e);
    }
}

/// <summary>进程列表行:包装快照,附加白名单/系统关键/可终止状态。</summary>
public partial class ProcessItemViewModel : ObservableObject
{
    public required ProcessSnapshot Snapshot { get; set; }
    public int Pid => Snapshot.Pid;
    public string Name => Snapshot.Name;
    public string MemoryText => $"{Snapshot.WorkingSetBytes / (1 << 20)} MB";
    public string? Path => Snapshot.Path;
    public string CpuText => $"{CpuPercent:0.0}%";
    public string StatusText => Snapshot.HasVisibleWindow ? "前台" : "后台";
    public string SignatureText => Snapshot.SignatureStatus switch
    {
        ProcessSignatureStatus.Signed => Locator.L10n["Processes.Signature.Signed"],
        ProcessSignatureStatus.Unsigned => Locator.L10n["Processes.Signature.Unsigned"],
        _ => Locator.L10n["Processes.Signature.Unknown"]
    };

    /// <summary>系统关键进程(csrss 等):不可加白,UI 灰显。初始化后不变。</summary>
    public required bool IsCritical { get; init; }
    public bool IsNotCritical => !IsCritical;

    /// <summary>是否已在白名单中。加入白名单后需通知 UI 更新勾选。</summary>
    [ObservableProperty] private bool _isExcluded;

    /// <summary>L3 勾选:仅 CanKill 行可选中。</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>FR-2.3:非系统关键/非白名单/非防误杀/非前台才可终止。</summary>
    [ObservableProperty] private bool _canKill;

    public double CpuPercent { get; private set; }

    public void UpdateSnapshot(ProcessSnapshot snapshot, double cpuPercent)
    {
        var previous = Snapshot;
        var previousCpuText = CpuText;
        Snapshot = snapshot;
        CpuPercent = cpuPercent;
        if (previous.Pid != snapshot.Pid) OnPropertyChanged(nameof(Pid));
        if (!string.Equals(previous.Name, snapshot.Name, StringComparison.Ordinal)) OnPropertyChanged(nameof(Name));
        if (previous.WorkingSetBytes != snapshot.WorkingSetBytes) OnPropertyChanged(nameof(MemoryText));
        if (!string.Equals(previous.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase)) OnPropertyChanged(nameof(Path));
        if (!string.Equals(previousCpuText, CpuText, StringComparison.Ordinal)) OnPropertyChanged(nameof(CpuText));
        if (previous.HasVisibleWindow != snapshot.HasVisibleWindow) OnPropertyChanged(nameof(StatusText));
        if (previous.SignatureStatus != snapshot.SignatureStatus) OnPropertyChanged(nameof(SignatureText));
    }
}

public partial class ProcessesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1.5);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private Dictionary<int, ProcessSample> _previousSamples = new();

    public ProcessItemCollection Items { get; } = new();

    /// <summary>FR-2.7 后悔药:最近被本应用结束的进程(新的在前)。</summary>
    public ObservableCollection<KillRecord> KillLogRecords { get; } = new();
    public bool HasKillLog => KillLogRecords.Count > 0;
    public bool KillLogEmpty => KillLogRecords.Count == 0;

    /// <summary>终止/恢复的结果状态文本。</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>当前是否正在抓取一批进程样本。</summary>
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>页面可见时每 1.5 秒更新一次,模拟任务管理器的实时列表。</summary>
    [ObservableProperty] private bool _isMonitoring;

    [ObservableProperty] private string _lastUpdatedText = "—";

    public string LiveSummaryText =>
        $"{(IsMonitoring ? "实时监控中" : "监控已暂停")} · {Items.Count} 个进程 · 最近更新 {LastUpdatedText}";

    /// <summary>开始后台轮询。页面导航回来时可重复调用,不会启动多个轮询任务。</summary>
    public void StartMonitoring()
    {
        if (IsMonitoring && _monitorTask is { IsCompleted: false }) return;
        var monitorCts = new CancellationTokenSource();
        _monitorCts = monitorCts;
        IsMonitoring = true;
        _monitorTask = MonitorLoopAsync(monitorCts);
    }

    /// <summary>页面离开时停止轮询,避免隐藏页面继续占用进程枚举和 CPU。</summary>
    public void StopMonitoring()
    {
        var monitorCts = _monitorCts;
        _monitorCts = null;
        monitorCts?.Cancel();
        IsMonitoring = false;
        OnPropertyChanged(nameof(LiveSummaryText));
    }

    private async Task MonitorLoopAsync(CancellationTokenSource monitorCts)
    {
        var cancellationToken = monitorCts.Token;
        try
        {
            await RefreshCoreAsync(cancellationToken, forceResort: false);
            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RefreshCoreAsync(cancellationToken, forceResort: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 页面离开时的正常退出。
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(monitorCts, _monitorCts))
                IsMonitoring = false;
            monitorCts.Dispose();
            OnPropertyChanged(nameof(LiveSummaryText));
        }
    }

    /// <summary>手动刷新等待门闩空出后强制执行并恢复内存降序;与实时轮询共享门闩,不会并发枚举进程。</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RefreshCoreAsync(CancellationToken.None, forceResort: true, waitForGate: true);

    private bool CanRefresh() => !IsRefreshing;

    partial void OnIsRefreshingChanged(bool value) => RefreshCommand.NotifyCanExecuteChanged();

    private async Task RefreshCoreAsync(CancellationToken cancellationToken, bool forceResort, bool waitForGate = false)
    {
        // 定时轮询在忙碌时直接跳过本次;用户手动触发(刷新/终止后)则排队等待,避免点了没反应
        if (waitForGate) await _refreshGate.WaitAsync(cancellationToken);
        else if (!await _refreshGate.WaitAsync(0, cancellationToken)) return;
        IsRefreshing = true;
        try
        {
            // Process.MainModule/FileName 和 TotalProcessorTime 可能因权限或退出中的进程而变慢,
            // 全部在线程池抓取,UI 线程只做轻量的差量更新。
            var rows = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 与 Windows 任务管理器一致显示全部当前进程,包含小工作集和后台进程。
                var snaps = Locator.Native.GetProcessSnapshots()
                    .OrderByDescending(p => p.WorkingSetBytes)
                    .ToList();
                var killable = Locator.Terminator.FilterCandidates(snaps).ToHashSet();
                return snaps.Select(p => new ProcessRowData(
                    p,
                    Locator.Whitelist.IsExcluded(p.Name),
                    Locator.Whitelist.IsSystemCritical(p.Name),
                    killable.Contains(p.Pid))).ToList();
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            ApplyRows(rows, forceResort);
            RefreshKillLog();
            TerminateSelectedCommand.NotifyCanExecuteChanged();
            StatusText = "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 页面离开时的正常退出。
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    internal void ApplyRows(IReadOnlyList<ProcessRowData> rows, bool forceResort)
    {
        var now = DateTimeOffset.Now;
        var incoming = rows.ToDictionary(r => r.Snapshot.Pid);
        var existing = Items.ToDictionary(i => i.Pid);

        // 只移除已经退出的进程,不 Clear 集合,从而保留 DataGrid 的行容器、选中状态和滚动位置。
        foreach (var item in Items.Where(i => !incoming.ContainsKey(i.Pid)).ToList())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            Items.Remove(item);
        }

        if (existing.Count == 0)
            Items.AddRange(rows.Select(row => CreateItem(row, now)));
        else
        {
            foreach (var row in rows)
            {
                if (existing.TryGetValue(row.Snapshot.Pid, out var item))
                {
                    var cpu = CalculateCpuPercent(row.Snapshot, now);
                    item.UpdateSnapshot(row.Snapshot, cpu);
                    item.IsExcluded = row.IsExcluded;
                    item.CanKill = row.CanKill;
                    if (!item.CanKill) item.IsSelected = false;
                }
                else
                {
                    Items.Add(CreateItem(row, now));
                }
            }
        }

        // 实时刷新原地更新不重排(避免行容器抖动);手动刷新时按最新快照恢复内存降序,
        // 用 Move 而不是 Clear+Add,保留选中状态、滚动位置和行容器。
        if (forceResort && Items.Count > 1)
        {
            var orderIndex = new Dictionary<int, int>(rows.Count);
            for (int i = 0; i < rows.Count; i++) orderIndex[rows[i].Snapshot.Pid] = i;
            for (int target = 0; target < Items.Count; target++)
            {
                int current = -1;
                for (int j = target; j < Items.Count; j++)
                {
                    if (orderIndex.TryGetValue(Items[j].Pid, out var idx) && idx == target) { current = j; break; }
                }
                if (current > target) Items.Move(current, target);
            }
        }

        _previousSamples = rows.ToDictionary(
            r => r.Snapshot.Pid,
            r => new ProcessSample(r.Snapshot.TotalProcessorTime, now, r.Snapshot.Name));
        LastUpdatedText = now.ToString("HH:mm:ss");
        OnPropertyChanged(nameof(LiveSummaryText));
    }

    private ProcessItemViewModel CreateItem(ProcessRowData row, DateTimeOffset now)
    {
        var item = new ProcessItemViewModel
        {
            Snapshot = row.Snapshot,
            IsExcluded = row.IsExcluded,
            IsCritical = row.IsCritical,
            CanKill = row.CanKill
        };
        item.UpdateSnapshot(row.Snapshot, CalculateCpuPercent(row.Snapshot, now));
        item.PropertyChanged += OnItemPropertyChanged;
        return item;
    }

    private double CalculateCpuPercent(ProcessSnapshot current, DateTimeOffset now)
    {
        if (!_previousSamples.TryGetValue(current.Pid, out var previous) ||
            !string.Equals(previous.Name, current.Name, StringComparison.OrdinalIgnoreCase))
            return 0;

        var wall = now - previous.Timestamp;
        var cpu = current.TotalProcessorTime - previous.CpuTime;
        if (wall <= TimeSpan.Zero || cpu <= TimeSpan.Zero) return 0;
        return Math.Clamp(cpu.TotalMilliseconds / wall.TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
    }

    private sealed record ProcessSample(TimeSpan CpuTime, DateTimeOffset Timestamp, string Name);

    internal sealed record ProcessRowData(
        ProcessSnapshot Snapshot,
        bool IsExcluded,
        bool IsCritical,
        bool CanKill);

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessItemViewModel.IsSelected))
            TerminateSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanTerminateSelected))]
    private async Task TerminateSelectedAsync()
    {
        try
        {
            var targets = Items.Where(i => i.IsSelected && i.CanKill).ToList();
            if (targets.Count == 0) return;

            var rows = targets.Select(t => new TerminateConfirmItem(
                t.Snapshot.Pid, t.Name, t.Path, t.Snapshot.WorkingSetBytes,
                Locator.Unsaved.HasUnsavedSigns(t.Snapshot.Pid))).ToList();

            var dlg = new TerminateConfirmDialog(rows) { Owner = System.Windows.Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            var r = await Locator.Terminator.TerminateAsync(dlg.SelectedPids);
            int ok = r.Items.Count(i => i.Success);
            int fail = r.Items.Count - ok;
            if (ok > 0)
                Locator.History.Record(new CleanHistoryEntry(
                    DateTimeOffset.Now, CleanLevel.L3, r.FreedBytes, ok, CleanTrigger.Manual));
            StatusText = string.Format(Locator.L10n["L3.Result"], ok, r.FreedBytes / (1 << 20), fail);
            await RefreshCoreAsync(CancellationToken.None, forceResort: true, waitForGate: true);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanTerminateSelected() => Items.Any(i => i.IsSelected && i.CanKill);

    [RelayCommand]
    private void RestoreAsync(KillRecord? record)
    {
        if (record == null) return;
        try
        {
            StatusText = Locator.L10n[Locator.KillLog.Restart(record) ? "L3.Restored" : "L3.RestoreFailed"];
        }
        catch
        {
            StatusText = Locator.L10n["L3.RestoreFailed"];
        }
    }

    private void RefreshKillLog()
    {
        KillLogRecords.Clear();
        foreach (var r in Locator.KillLog.Records) KillLogRecords.Add(r);
        OnPropertyChanged(nameof(HasKillLog));
        OnPropertyChanged(nameof(KillLogEmpty));
    }

    [RelayCommand(CanExecute = nameof(CanAddToWhitelist))]
    private void AddToWhitelist(ProcessItemViewModel? item)
    {
        if (item == null || item.IsCritical || item.IsExcluded) return;
        Locator.Whitelist.Add(item.Name);
        item.IsExcluded = true;
        item.CanKill = false;
        item.IsSelected = false;
        AddToWhitelistCommand.NotifyCanExecuteChanged();
        TerminateSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddToWhitelist(ProcessItemViewModel? item) =>
        item is not null && !item.IsCritical && !item.IsExcluded;

    public void Dispose()
    {
        StopMonitoring();
        _refreshGate.Dispose();
    }
}
