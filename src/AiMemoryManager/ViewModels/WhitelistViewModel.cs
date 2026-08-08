using System.Collections.ObjectModel;
using System.Windows;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using Microsoft.Win32;

namespace AiMemoryManager.ViewModels;

/// <summary>大模型返回的白名单建议行。勾选只代表用户准备确认，不会立即修改白名单。</summary>
public partial class WhitelistAdviceItemViewModel : ObservableObject
{
    private readonly WhitelistAdvice _advice;

    public WhitelistAdviceItemViewModel(WhitelistAdvice advice)
        => _advice = advice;

    public string ProcessName => _advice.ProcessName;
    public string PathText => string.IsNullOrWhiteSpace(_advice.Path) ? "-" : _advice.Path!;
    public string MemoryText => $"{_advice.WorkingSetBytes / (1 << 20)} MB";
    public string Reason => _advice.Reason;
    public bool Recommended => _advice.Recommended;
    public bool NotRecommended => !Recommended;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isAdded;
}

public partial class WhitelistViewModel : ObservableObject
{
    public ObservableCollection<string> Items { get; } = new();
    public ObservableCollection<WhitelistAdviceItemViewModel> AdviceItems { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _hasAdviceItems;
    [ObservableProperty] private bool _adviceIsEmpty = true;
    [ObservableProperty] private bool _isAnalyzingAdvice;
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newName = "";

    // FR-7.2 防误杀名单(第二列表):名单内进程永远不会被"结束进程"终止
    public ObservableCollection<string> NoKillItems { get; } = new();

    // 黑名单自动终止
    public ObservableCollection<string> BlacklistItems { get; } = new();
    [ObservableProperty] private bool _blacklistHasItems;
    [ObservableProperty] private bool _blacklistIsEmpty = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddBlacklistCommand))]
    private string _newBlacklistName = "";

    [ObservableProperty]
    private bool _blacklistAutoTerminateEnabled;
    [ObservableProperty] private bool _noKillHasItems;
    [ObservableProperty] private bool _noKillIsEmpty = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNoKillCommand))]
    private string _newNoKillName = "";

    public WhitelistViewModel()
    {
        RefreshList();
        RefreshNoKillList();
        RefreshBlacklistList();
        _blacklistAutoTerminateEnabled = Locator.Settings.Current.BlacklistAutoTerminateEnabled;
    }

    private void RefreshList()
    {
        Items.Clear();
        foreach (var n in Locator.Whitelist.Excluded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Items.Add(n);
        HasItems = Items.Count > 0;
        IsEmpty = !HasItems;
    }

    private void RefreshBlacklistList()
    {
        BlacklistItems.Clear();
        foreach (var n in Locator.Blacklist.Items.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            BlacklistItems.Add(n);
        BlacklistHasItems = BlacklistItems.Count > 0;
        BlacklistIsEmpty = !BlacklistHasItems;
    }
    private void RefreshNoKillList()
    {
        NoKillItems.Clear();
        foreach (var n in Locator.Whitelist.NoKill.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            NoKillItems.Add(n);
        NoKillHasItems = NoKillItems.Count > 0;
        NoKillIsEmpty = !NoKillHasItems;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        Locator.Whitelist.Add(NewName);
        NewName = "";
        RefreshList();
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand]
    private void Remove(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        Locator.Whitelist.Remove(name);
        RefreshList();
    }

    [RelayCommand]
    private async Task AnalyzeAdviceAsync()
    {
        if (IsAnalyzingAdvice) return;
        IsAnalyzingAdvice = true;
        StatusText = Locator.L10n["Whitelist.AdviceAnalyzing"];
        try
        {
            var result = await Locator.WhitelistAdvice.AnalyzeAsync();
            foreach (var item in AdviceItems)
                item.PropertyChanged -= OnAdviceItemPropertyChanged;
            AdviceItems.Clear();
            foreach (var advice in result.Suggestions)
            {
                var item = new WhitelistAdviceItemViewModel(advice)
                {
                    // 推荐项预先勾选，但仍必须点击“加入选中白名单”才会落盘。
                    IsSelected = advice.Recommended
                };
                item.PropertyChanged += OnAdviceItemPropertyChanged;
                AdviceItems.Add(item);
            }
            HasAdviceItems = AdviceItems.Count > 0;
            AdviceIsEmpty = !HasAdviceItems;
            StatusText = AdviceItems.Count == 0
                ? Locator.L10n["Whitelist.AdviceEmpty"]
                : string.Format(Locator.L10n["Whitelist.AdviceDone"], AdviceItems.Count,
                    result.Usage.InputTokens + result.Usage.OutputTokens);
            AddSelectedAdviceCommand.NotifyCanExecuteChanged();
        }
        catch (InvalidOperationException)
        {
            StatusText = Locator.L10n["Analysis.NoProfile"];
        }
        catch (Exception ex)
        {
            // API 未配置、网络错误或模型输出异常都只显示在页面上，不能让窗口退出。
            StatusText = ex.Message;
        }
        finally
        {
            IsAnalyzingAdvice = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddSelectedAdvice))]
    private void AddSelectedAdvice()
    {
        var targets = AdviceItems
            .Where(i => i.IsSelected && i.Recommended && !i.IsAdded)
            .Where(i => !Locator.Whitelist.IsExcluded(i.ProcessName))
            .Where(i => !Locator.Whitelist.IsSystemCritical(i.ProcessName))
            .ToList();
        if (targets.Count == 0) return;

        foreach (var item in targets)
        {
            Locator.Whitelist.Add(item.ProcessName);
            item.IsAdded = true;
            item.IsSelected = false;
        }
        RefreshList();
        StatusText = string.Format(Locator.L10n["Whitelist.AdviceAdded"], targets.Count);
        AddSelectedAdviceCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddSelectedAdvice() => AdviceItems.Any(i =>
        i.IsSelected && i.Recommended && !i.IsAdded && !Locator.Whitelist.IsExcluded(i.ProcessName));

    private void OnAdviceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WhitelistAdviceItemViewModel.IsSelected) ||
            e.PropertyName == nameof(WhitelistAdviceItemViewModel.IsAdded))
            AddSelectedAdviceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddBlacklist))]
    private void AddBlacklist()
    {
        if (Locator.Blacklist.TryAdd(NewBlacklistName, out var reason))
        {
            NewBlacklistName = "";
            StatusText = Locator.L10n["Blacklist.Added"];
            RefreshBlacklistList();
        }
        else
        {
            StatusText = reason;
        }
    }

    private bool CanAddBlacklist() => !string.IsNullOrWhiteSpace(NewBlacklistName);

    [RelayCommand]
    private void RemoveBlacklist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Locator.Blacklist.Remove(name);
        RefreshBlacklistList();
        StatusText = Locator.L10n["Blacklist.Removed"];
    }

    partial void OnBlacklistAutoTerminateEnabledChanged(bool value)
    {
        if (value)
        {
            var result = MessageBox.Show(
                Application.Current.MainWindow,
                Locator.L10n["Blacklist.EnableConfirm"],
                Locator.L10n["Blacklist.Title"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                _blacklistAutoTerminateEnabled = false;
                OnPropertyChanged(nameof(BlacklistAutoTerminateEnabled));
                return;
            }
        }

        Locator.Settings.Current.BlacklistAutoTerminateEnabled = value;
        Locator.Settings.Save();
        Locator.ProcessStartMonitor.SetEnabled(value);
        StatusText = value ? Locator.L10n["Blacklist.Enabled"] : Locator.L10n["Blacklist.Disabled"];
    }
    [RelayCommand(CanExecute = nameof(CanAddNoKill))]
    private void AddNoKill()
    {
        Locator.Whitelist.AddNoKill(NewNoKillName);
        NewNoKillName = "";
        RefreshNoKillList();
        RefreshBlacklistList();
    }

    private bool CanAddNoKill() => !string.IsNullOrWhiteSpace(NewNoKillName);

    [RelayCommand]
    private void RemoveNoKill(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        Locator.Whitelist.RemoveNoKill(name);
        RefreshNoKillList();
        RefreshBlacklistList();
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