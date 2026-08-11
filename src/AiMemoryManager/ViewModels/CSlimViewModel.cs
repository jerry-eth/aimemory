using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Views;

namespace AiMemoryManager.ViewModels;

public sealed record FolderSizeRow(FolderSizeInfo Info)
{
    public string Path => Info.Path;
    public string SizeText => FormatBytes(Info.SizeBytes);
    public string Category => Info.Category.ToString();
    public int FileCount => Info.FileCount;
    public string PartialText => Info.IsPartial ? $"部分跳过 {Info.SkippedCount} 项" : "";
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        _ => $"{bytes / 1024d / 1024 / 1024:0.00} GB"
    };
}

public partial class CleanableRowViewModel : ObservableObject
{
    public required DiskCleanableItem Item { get; init; }
    public string Path => Item.Path;
    public string Reason => Item.Reason;
    public string EstText => FolderSizeRowExtensions.FormatBytesForDisplay(Item.EstBytes);
    public string Risk => Item.Risk;
    [ObservableProperty] private bool _isSelected;
}

public partial class MigratableRowViewModel : ObservableObject
{
    public required DiskMigratableItem Item { get; init; }
    public string Path => Item.Path;
    public string Reason => Item.Reason;
    public string TargetDrive => Item.TargetDrive;
    public string Risk => Item.Risk;
    [ObservableProperty] private bool _isSelected;
}

public sealed record MigrationLogRow(MigrationLogEntry Entry)
{
    public string TimeText => Entry.Time.ToLocalTime().ToString("MM-dd HH:mm");
    public string Source => Entry.Source;
    public string Target => Entry.Target;
    public bool Reverted => Entry.Reverted;
    public string RevertedText => Entry.Reverted ? Locator.L10n["Slim.Reverted"] : Entry.State.ToString();
    public string StateText => Entry.State.ToString();
}

public static class FolderSizeRowExtensions
{
    public static string FormatBytesForDisplay(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        _ => $"{bytes / 1024d / 1024 / 1024:0.00} GB"
    };
}

/// <summary>C 盘瘦身工作台：扫描、无 LLM 本地建议、回收站清理、事务迁移和回退。</summary>
public partial class CSlimViewModel : ObservableObject
{
    public ObservableCollection<FolderSizeRow> ScanResults { get; } = new();
    public ObservableCollection<CleanableRowViewModel> Cleanable { get; } = new();
    public ObservableCollection<MigratableRowViewModel> Migratable { get; } = new();
    public ObservableCollection<MigrationLogRow> MigrationLog { get; } = new();

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _scanProgressText = "";
    [ObservableProperty] private string _diskSummaryText = "";
    [ObservableProperty] private string _adviceSourceText = "";
    [ObservableProperty] private string _lastScanText = "";
    [ObservableProperty] private double _scanPercent;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _isOperating;
    [ObservableProperty] private bool _isScanCanceled;
    [ObservableProperty] private bool _hasScanResults;
    [ObservableProperty] private bool _hasMigrationLog;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty] private int _failedCount;

    private CancellationTokenSource? _scanCts;

    public CSlimViewModel() => RefreshMigrationLog();

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        IsScanCanceled = false;
        ScanPercent = 0;
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        StatusText = Locator.L10n["Slim.Scanning"];
        try
        {
            var candidates = new DiskCandidateProvider().GetCandidates();
            var progress = new Progress<DiskScanProgress>(p =>
            {
                ScanProgressText = $"{p.Stage}{(string.IsNullOrWhiteSpace(p.CurrentPath) ? "" : $"：{p.CurrentPath}")}";
                ScanPercent = p.Percent;
            });
            var result = await Locator.DiskScan.ScanDetailedAsync(candidates, progress, _scanCts.Token);
            ScanResults.Clear();
            Cleanable.Clear();
            Migratable.Clear();
            FailedCount = result.Issues.Count;
            SkippedCount = result.Items.Sum(i => i.SkippedCount) + result.Issues.Count;
            foreach (var row in result.Items.Where(r => r.SizeBytes > 0).OrderByDescending(r => r.SizeBytes).Take(100))
                ScanResults.Add(new FolderSizeRow(row));
            HasScanResults = ScanResults.Count > 0;
            IsScanCanceled = result.IsCanceled;
            LastScanText = result.FinishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            DiskSummaryText = result.Space is null
                ? "C 盘空间：无法读取"
                : $"C 盘 {FolderSizeRowExtensions.FormatBytesForDisplay(result.Space.FreeBytes)} 可用 / {FolderSizeRowExtensions.FormatBytesForDisplay(result.Space.TotalBytes)}，占用 {result.Space.UsedPercent:0.0}%（{result.Space.Pressure}）";
            StatusText = result.IsCanceled
                ? $"扫描已取消，保留 {ScanResults.Count} 项结果"
                : HasScanResults ? $"扫描完成，共 {ScanResults.Count} 项；可跳过 {SkippedCount} 项" : Locator.L10n["Slim.Empty"];
        }
        catch (OperationCanceledException)
        {
            IsScanCanceled = true;
            StatusText = "扫描已取消，未执行任何删除或迁移";
        }
        catch (Exception ex) { StatusText = $"扫描失败：{ex.Message}"; }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsAnalyzing || ScanResults.Count == 0)
        {
            if (ScanResults.Count == 0) StatusText = Locator.L10n["Slim.Empty"];
            return;
        }
        IsAnalyzing = true;
        StatusText = Locator.L10n["Analysis.Running"];
        try
        {
            var advice = await Locator.DiskAdvice.AnalyzeWithFallbackAsync(ScanResults.Select(r => r.Info).ToList());
            Cleanable.Clear();
            foreach (var item in advice.Cleanable) Cleanable.Add(new CleanableRowViewModel { Item = item });
            Migratable.Clear();
            foreach (var item in advice.Migratable) Migratable.Add(new MigratableRowViewModel { Item = item });
            AdviceSourceText = advice.StatusMessage ?? advice.Source.ToString();
            StatusText = AdviceSourceText;
        }
        catch (OperationCanceledException) { StatusText = "分析已取消"; }
        catch (Exception ex) { StatusText = $"分析失败：{ex.Message}"; }
        finally { IsAnalyzing = false; }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsOperating) return;
        var selected = Cleanable.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;
        var dlg = new SlimConfirmDialog(
            Locator.L10n["Slim.DeleteSelected"],
            string.Format(Locator.L10n["Slim.ConfirmDelete"], selected.Count),
            Locator.L10n["Slim.DeleteSelected"]) { Owner = App.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        IsOperating = true;
        try
        {
            var report = await Locator.RecycleBin.DeleteDirectoriesAsync(
                selected.Select(r => (r.Path, r.Item.Category)));
            foreach (var row in selected.Where(r => report.Items.Any(i => i.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase) && i.Status == DiskOperationStatus.Succeeded)).ToList())
            {
                Cleanable.Remove(row);
                RemoveScanRow(row.Path);
            }
            StatusText = $"已完成：成功 {report.SucceededCount}，跳过 {report.SkippedCount}，失败 {report.FailedCount}，预计释放 {FolderSizeRowExtensions.FormatBytesForDisplay(report.SucceededBytes)}";
        }
        catch (Exception ex) { StatusText = $"清理失败：{ex.Message}"; }
        finally { IsOperating = false; }
    }

    [RelayCommand]
    private async Task MigrateSelectedAsync()
    {
        if (IsOperating) return;
        var selected = Migratable.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;
        var blocked = selected.SelectMany(r => Locator.Migration.GetBlockingProcesses(r.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (blocked.Count > 0)
        {
            StatusText = string.Format(Locator.L10n["Slim.Blocked"], string.Join(", ", blocked));
            return;
        }
        string targets = string.Join(", ", selected.Select(r => r.TargetDrive).Distinct());
        var dlg = new SlimConfirmDialog(
            Locator.L10n["Slim.MigrateSelected"],
            string.Format(Locator.L10n["Slim.ConfirmMigrate"], selected.Count, targets),
            Locator.L10n["Slim.MigrateSelected"]) { Owner = App.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        IsOperating = true;
        int ok = 0, failed = 0;
        string? firstError = null;
        try
        {
            foreach (var row in selected)
            {
                try
                {
                    await Locator.Migration.MigrateAsync(row.Path, row.TargetDrive.TrimEnd('\\') + "\\");
                    ok++;
                    Migratable.Remove(row);
                    RemoveScanRow(row.Path);
                }
                catch (Exception ex) { failed++; firstError ??= ex.Message; }
            }
            StatusText = $"迁移完成：成功 {ok}，失败 {failed}" + (firstError is null ? "" : $"；{firstError}");
            RefreshMigrationLog();
        }
        catch (Exception ex) { StatusText = $"迁移失败：{ex.Message}"; }
        finally { IsOperating = false; }
    }

    [RelayCommand]
    private async Task RevertAsync(MigrationLogRow? row)
    {
        if (row is null || IsOperating) return;
        IsOperating = true;
        try
        {
            bool ok = await Locator.Migration.RevertAsync(row.Entry);
            StatusText = ok ? string.Format(Locator.L10n["Slim.RevertDone"], row.Source) : Locator.L10n["Slim.RevertFailed"];
            RefreshMigrationLog();
        }
        catch (Exception ex) { StatusText = $"回退失败：{ex.Message}"; }
        finally { IsOperating = false; }
    }

    private void RemoveScanRow(string path)
    {
        var row = ScanResults.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        if (row != null) ScanResults.Remove(row);
        HasScanResults = ScanResults.Count > 0;
    }

    private void RefreshMigrationLog()
    {
        MigrationLog.Clear();
        foreach (var entry in Locator.Migration.Log) MigrationLog.Add(new MigrationLogRow(entry));
        HasMigrationLog = MigrationLog.Count > 0;
    }
}

