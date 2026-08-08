using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Services;
using Microsoft.Win32;

namespace AiMemoryManager.ViewModels;

public partial class WhitelistViewModel : ObservableObject
{
    public ObservableCollection<string> Items { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private bool _isEmpty = true;

    // FR-7.2 防误杀名单(第二列表):名单内进程永远不会被"结束进程"终止
    public ObservableCollection<string> NoKillItems { get; } = new();

    [ObservableProperty] private bool _noKillHasItems;
    [ObservableProperty] private bool _noKillIsEmpty = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNoKillCommand))]
    private string _newNoKillName = "";

    public WhitelistViewModel()
    {
        RefreshList();
        RefreshNoKillList();
    }

    private void RefreshList()
    {
        Items.Clear();
        foreach (var n in Locator.Whitelist.Excluded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Items.Add(n);
        HasItems = Items.Count > 0;
        IsEmpty = !HasItems;
    }

    private void RefreshNoKillList()
    {
        NoKillItems.Clear();
        foreach (var n in Locator.Whitelist.NoKill.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            NoKillItems.Add(n);
        NoKillHasItems = NoKillItems.Count > 0;
        NoKillIsEmpty = !NoKillHasItems;
    }

    [RelayCommand]
    private void Remove(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        Locator.Whitelist.Remove(name);
        RefreshList();
    }

    [RelayCommand(CanExecute = nameof(CanAddNoKill))]
    private void AddNoKill()
    {
        Locator.Whitelist.AddNoKill(NewNoKillName);
        NewNoKillName = "";
        RefreshNoKillList();
    }

    private bool CanAddNoKill() => !string.IsNullOrWhiteSpace(NewNoKillName);

    [RelayCommand]
    private void RemoveNoKill(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        Locator.Whitelist.RemoveNoKill(name);
        RefreshNoKillList();
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Title = Locator.L10n["Whitelist.ImportTitle"],
            Filter = Locator.L10n["Whitelist.FileFilter"],
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        Locator.Whitelist.Import(dlg.FileName);
        RefreshList();
    }

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = Locator.L10n["Whitelist.ExportTitle"],
            Filter = Locator.L10n["Whitelist.FileFilter"],
            FileName = "whitelist.txt"
        };
        if (dlg.ShowDialog() != true) return;
        Locator.Whitelist.Export(dlg.FileName);
    }
}
