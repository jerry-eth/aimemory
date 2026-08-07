using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

/// <summary>进程列表行:包装快照,附加白名单/系统关键状态。</summary>
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
}

public partial class ProcessesViewModel : ObservableObject
{
    public ObservableCollection<ProcessItemViewModel> Items { get; } = new();

    /// <summary>重新枚举进程:只列工作集 &gt; 10MB 的,按内存降序。</summary>
    [RelayCommand]
    private void Refresh()
    {
        Items.Clear();
        foreach (var p in Locator.Native.GetProcessSnapshots()
                     .Where(p => p.WorkingSetBytes > 10L << 20)
                     .OrderByDescending(p => p.WorkingSetBytes))
        {
            Items.Add(new ProcessItemViewModel
            {
                Snapshot = p,
                IsExcluded = Locator.Whitelist.IsExcluded(p.Name),
                IsCritical = Locator.Whitelist.IsSystemCritical(p.Name)
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddToWhitelist))]
    private void AddToWhitelist(ProcessItemViewModel? item)
    {
        if (item == null || item.IsCritical || item.IsExcluded) return;
        Locator.Whitelist.Add(item.Name);
        item.IsExcluded = true;
    }

    /// <summary>系统关键进程与已加白进程禁用右键菜单项。</summary>
    private bool CanAddToWhitelist(ProcessItemViewModel? item) =>
        item is not null && !item.IsCritical && !item.IsExcluded;
}
