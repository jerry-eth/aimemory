using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Views;

namespace AiMemoryManager.ViewModels;

/// <summary>进程列表行:包装快照,附加白名单/系统关键/可终止状态。</summary>
public partial class ProcessItemViewModel : ObservableObject
{
    public required ProcessSnapshot Snapshot { get; init; }
    public string Name => Snapshot.Name;
    public string MemoryText => $"{Snapshot.WorkingSetBytes / (1 << 20)} MB";
    public string? Path => Snapshot.Path;

    /// <summary>系统关键进程(csrss 等):不可加白,UI 灰显。初始化后不变。</summary>
    public required bool IsCritical { get; init; }
    public bool IsNotCritical => !IsCritical;

    /// <summary>是否已在白名单中。加入白名单后需通知 UI 更新勾选。</summary>
    [ObservableProperty] private bool _isExcluded;

    /// <summary>L3 勾选:仅 CanKill 行可选中。</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>FR-2.3:非系统关键/非白名单/非防误杀/非前台才可终止(FilterCandidates 批量算一次后映射回行)。</summary>
    [ObservableProperty] private bool _canKill;
}

public partial class ProcessesViewModel : ObservableObject
{
    public ObservableCollection<ProcessItemViewModel> Items { get; } = new();

    /// <summary>FR-2.7 后悔药:最近被本应用结束的进程(新的在前)。</summary>
    public ObservableCollection<KillRecord> KillLogRecords { get; } = new();
    public bool HasKillLog => KillLogRecords.Count > 0;
    public bool KillLogEmpty => KillLogRecords.Count == 0;

    /// <summary>终止/恢复的结果状态文本。</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>进程枚举在线程池执行,避免 System.Diagnostics.Process 的权限查询阻塞界面线程。</summary>
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>重新枚举进程:只列工作集 &gt; 10MB 的,按内存降序。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            // Process.MainModule/FileName 可能因权限或退出中的进程而变慢,绝不能在 UI 线程执行。
            var rows = await Task.Run(() =>
            {
                var snaps = Locator.Native.GetProcessSnapshots()
                    .Where(p => p.WorkingSetBytes > 10L << 20)
                    .OrderByDescending(p => p.WorkingSetBytes)
                    .ToList();
                // 可终止判定批量算一次(内部含一次快照),再按 PID 映射回行,避免逐行重复快照
                var killable = Locator.Terminator.FilterCandidates(snaps.Select(p => p.Pid).ToList()).ToHashSet();
                return snaps.Select(p => new ProcessRowData(
                    p,
                    Locator.Whitelist.IsExcluded(p.Name),
                    Locator.Whitelist.IsSystemCritical(p.Name),
                    killable.Contains(p.Pid))).ToList();
            });

            Items.Clear();
            foreach (var row in rows)
            {
                var item = new ProcessItemViewModel
                {
                    Snapshot = row.Snapshot,
                    IsExcluded = row.IsExcluded,
                    IsCritical = row.IsCritical,
                    CanKill = row.CanKill
                };
                item.PropertyChanged += OnItemPropertyChanged;
                Items.Add(item);
            }
            RefreshKillLog();
            TerminateSelectedCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private sealed record ProcessRowData(
        ProcessSnapshot Snapshot,
        bool IsExcluded,
        bool IsCritical,
        bool CanKill);

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 勾选变化 → "结束选中进程"按钮可用态重估
        if (e.PropertyName == nameof(ProcessItemViewModel.IsSelected))
            TerminateSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanTerminateSelected))]
    private async Task TerminateSelectedAsync()
    {
        // async 命令:全程 try/catch,终止失败只落状态文本,不抛回 UI 线程
        try
        {
            var targets = Items.Where(i => i.IsSelected && i.CanKill).ToList();
            if (targets.Count == 0) return;

            // FR-2.6:打开对话框时逐个查未保存迹象,结果随清单传入对话框
            var rows = targets.Select(t => new TerminateConfirmItem(
                t.Snapshot.Pid, t.Name, t.Path, t.Snapshot.WorkingSetBytes,
                Locator.Unsaved.HasUnsavedSigns(t.Snapshot.Pid))).ToList();

            // L3 默认必须用户确认:无静默终止路径
            var dlg = new TerminateConfirmDialog(rows) { Owner = System.Windows.Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            var r = await Locator.Terminator.TerminateAsync(dlg.SelectedPids);
            int ok = r.Items.Count(i => i.Success);
            int fail = r.Items.Count - ok;
            // 成功终止后才记历史(Manual L3 的唯一记录点;Locator 故意不给 Terminator 挂历史订阅)
            if (ok > 0)
                Locator.History.Record(new CleanHistoryEntry(
                    DateTimeOffset.Now, CleanLevel.L3, r.FreedBytes, ok, CleanTrigger.Manual));
            StatusText = string.Format(Locator.L10n["L3.Result"], ok, r.FreedBytes / (1 << 20), fail);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanTerminateSelected() => Items.Any(i => i.IsSelected && i.CanKill);

    /// <summary>FR-2.7 后悔药:按记录的路径+参数重启进程。</summary>
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
        // 右键菜单对同一行重复打开时绑定不会重估,需主动刷新命令可用态
        AddToWhitelistCommand.NotifyCanExecuteChanged();
    }

    /// <summary>系统关键进程与已加白进程禁用右键菜单项。</summary>
    private bool CanAddToWhitelist(ProcessItemViewModel? item) =>
        item is not null && !item.IsCritical && !item.IsExcluded;
}
