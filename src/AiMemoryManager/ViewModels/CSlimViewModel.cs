using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Views;

namespace AiMemoryManager.ViewModels;

/// <summary>扫描结果行:大小/文件数预格式化,保留原始 FolderSizeInfo 供 LLM 分析。</summary>
public sealed record FolderSizeRow(FolderSizeInfo Info)
{
    public string Path => Info.Path;
    public string SizeText => $"{Info.SizeBytes / (1 << 20)} MB";
    public string Category => Info.Category.ToString();
    public int FileCount => Info.FileCount;
}

/// <summary>可清理建议行:勾选 + 预格式化预估释放。</summary>
public partial class CleanableRowViewModel : ObservableObject
{
    public required DiskCleanableItem Item { get; init; }
    public string Path => Item.Path;
    public string Reason => Item.Reason;
    public string EstText => $"{Item.EstBytes / (1 << 20)} MB";

    [ObservableProperty] private bool _isSelected;
}

/// <summary>可迁移建议行:勾选 + 目标盘。</summary>
public partial class MigratableRowViewModel : ObservableObject
{
    public required DiskMigratableItem Item { get; init; }
    public string Path => Item.Path;
    public string Reason => Item.Reason;
    public string TargetDrive => Item.TargetDrive;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>迁移记录行:包装 MigrationLogEntry,回退按钮仅对未回退项可见(Reverted 驱动显隐)。</summary>
public sealed record MigrationLogRow(MigrationLogEntry Entry)
{
    public string TimeText => Entry.Time.ToLocalTime().ToString("MM-dd HH:mm");
    public string Source => Entry.Source;
    public string Target => Entry.Target;
    public bool Reverted => Entry.Reverted;
    public string RevertedText => Locator.L10n["Slim.Reverted"];
}

/// <summary>
/// C 盘瘦身页 VM(FR-12):扫描 → LLM 建议 → 勾选删除(入回收站)/跨盘迁移 → 迁移记录回退。
/// 执行端不信 UI:删除前逐项重查 SystemPathGuard;迁移前逐项查占用,有占用整批中止。
/// 全部命令 try/catch,失败只落 StatusText;确认对话框照 Task 7 FluentWindow 先例。
/// </summary>
public partial class CSlimViewModel : ObservableObject
{
    public ObservableCollection<FolderSizeRow> ScanResults { get; } = new();
    public ObservableCollection<CleanableRowViewModel> Cleanable { get; } = new();
    public ObservableCollection<MigratableRowViewModel> Migratable { get; } = new();
    public ObservableCollection<MigrationLogRow> MigrationLog { get; } = new();

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _hasScanResults;
    [ObservableProperty] private bool _hasMigrationLog;

    public CSlimViewModel() => RefreshMigrationLog();

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = Locator.L10n["Slim.Scanning"];
        try
        {
            var candidates = new DiskCandidateProvider().GetCandidates();
            var results = await Locator.DiskScan.ScanAsync(candidates);
            ScanResults.Clear();
            // 按大小降序,过滤 0 项,展示 Top15(与 DiskCandidateProvider 注释口径一致)
            foreach (var r in results.Where(r => r.SizeBytes > 0)
                                     .OrderByDescending(r => r.SizeBytes).Take(15))
                ScanResults.Add(new FolderSizeRow(r));
            HasScanResults = ScanResults.Count > 0;
            StatusText = HasScanResults ? "" : Locator.L10n["Slim.Empty"];
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsAnalyzing) return;
        if (ScanResults.Count == 0) { StatusText = Locator.L10n["Slim.Empty"]; return; }
        if (Locator.Profiles.GetActive() is null)
        {
            StatusText = Locator.L10n["Slim.NoProfile"];
            return;
        }
        IsAnalyzing = true;
        StatusText = Locator.L10n["Analysis.Running"];
        try
        {
            var advice = await Locator.DiskAdvice.AnalyzeAsync(
                ScanResults.Select(r => r.Info).ToList());
            Cleanable.Clear();
            foreach (var c in advice.Cleanable)
                Cleanable.Add(new CleanableRowViewModel { Item = c });
            Migratable.Clear();
            foreach (var m in advice.Migratable)
                Migratable.Add(new MigratableRowViewModel { Item = m });
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally { IsAnalyzing = false; }
    }

    /// <summary>
    /// 删除选中(入回收站):逐项重查 SystemPathGuard(执行端不信 UI)→ 确认对话框 →
    /// 逐目录 SendToRecycleBin。删除循环保持 UI 线程:FileSystem.DeleteDirectory 的
    /// UIOption 依赖 STA,放线程池会静默失败。成功的行从建议与扫描结果中移除。
    /// </summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        try
        {
            var selected = Cleanable.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) return;

            // 执行端强制重查(FR-12.5):受保护项剔除并提示,不依赖建议侧过滤
            var guarded = selected.Where(r => SystemPathGuard.IsProtected(r.Path)).ToList();
            if (guarded.Count > 0)
            {
                StatusText = Locator.L10n["Slim.Protected"];
                selected = selected.Except(guarded).ToList();
                if (selected.Count == 0) return;
            }

            // 无静默删除路径:默认必须用户确认
            var dlg = new SlimConfirmDialog(
                Locator.L10n["Slim.DeleteSelected"],
                string.Format(Locator.L10n["Slim.ConfirmDelete"], selected.Count),
                Locator.L10n["Slim.DeleteSelected"])
            { Owner = App.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            int ok = 0;
            foreach (var row in selected)
            {
                if (!Locator.RecycleBin.DeleteDirectoryToRecycleBin(row.Path)) continue;
                ok++;
                Cleanable.Remove(row);
                RemoveScanRow(row.Path);
            }
            StatusText = string.Format(Locator.L10n["Slim.DeleteDone"], ok);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    /// <summary>
    /// 迁移选中:逐项占用预检(任一项被占用 → Slim.Blocked 整批中止)→ 确认对话框
    /// (文案含永久删除警告)→ 逐项 MigrateAsync(服务内再做守卫/占用/校验)→ 刷新迁移记录。
    /// </summary>
    [RelayCommand]
    private async Task MigrateSelectedAsync()
    {
        try
        {
            var selected = Migratable.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) return;

            // 占用预检(FR-12.5):每次迁移前都查,占用则中止整批
            var blocked = selected
                .SelectMany(r => Locator.Migration.GetBlockingProcesses(r.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (blocked.Count > 0)
            {
                StatusText = string.Format(Locator.L10n["Slim.Blocked"], string.Join(", ", blocked));
                return;
            }

            // 无静默迁移路径:默认必须用户确认(文案含永久删除警告)
            string targets = string.Join(", ", selected.Select(r => r.Item.TargetDrive).Distinct());
            var dlg = new SlimConfirmDialog(
                Locator.L10n["Slim.MigrateSelected"],
                string.Format(Locator.L10n["Slim.ConfirmMigrate"], selected.Count, targets),
                Locator.L10n["Slim.MigrateSelected"])
            { Owner = App.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            int ok = 0;
            string? firstError = null;
            foreach (var row in selected)
            {
                try
                {
                    // 目标盘补尾分隔符:"D:" 经 Path.Combine 会得到盘符相对路径,必须落成根目录
                    string targetRoot = row.Item.TargetDrive.TrimEnd('\\') + "\\";
                    await Locator.Migration.MigrateAsync(row.Path, targetRoot);
                    ok++;
                    Migratable.Remove(row);
                    RemoveScanRow(row.Path);
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;   // 单项失败不拖垮整批,首个错误落状态文本
                }
            }
            StatusText = string.Format(Locator.L10n["Slim.MigrateDone"], ok)
                       + (firstError != null ? $" · {firstError}" : "");
            RefreshMigrationLog();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RevertAsync(MigrationLogRow? row)
    {
        if (row == null) return;
        try
        {
            bool ok = await Locator.Migration.RevertAsync(row.Entry);
            StatusText = ok
                ? string.Format(Locator.L10n["Slim.RevertDone"], row.Source)
                : Locator.L10n["Slim.RevertFailed"];
            RefreshMigrationLog();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void RemoveScanRow(string path)
    {
        var row = ScanResults.FirstOrDefault(s =>
            string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        if (row != null) ScanResults.Remove(row);
        HasScanResults = ScanResults.Count > 0;
    }

    private void RefreshMigrationLog()
    {
        MigrationLog.Clear();
        foreach (var e in Locator.Migration.Log) MigrationLog.Add(new MigrationLogRow(e));
        HasMigrationLog = MigrationLog.Count > 0;
    }
}
