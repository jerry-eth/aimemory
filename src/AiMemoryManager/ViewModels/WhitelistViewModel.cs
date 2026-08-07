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

    public WhitelistViewModel() => RefreshList();

    private void RefreshList()
    {
        Items.Clear();
        foreach (var n in Locator.Whitelist.Excluded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Items.Add(n);
        HasItems = Items.Count > 0;
        IsEmpty = !HasItems;
    }

    [RelayCommand]
    private void Remove(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        Locator.Whitelist.Remove(name);
        RefreshList();
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
