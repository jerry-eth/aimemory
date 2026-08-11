using System.IO;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>异步、可取消、容错的候选目录扫描服务。</summary>
public class DiskScanService
{
    public Task<FolderSizeInfo> MeasureAsync(string path, DiskCategory category, CancellationToken ct = default)
        => Task.Run(() => Measure(path, category, ct), ct);

    /// <summary>兼容旧调用：批量测量，单项失败仍返回零结果。</summary>
    public async Task<IReadOnlyList<FolderSizeInfo>> ScanAsync(IEnumerable<DiskCandidate> candidates, CancellationToken ct = default)
    {
        var result = await ScanDetailedAsync(candidates, null, ct);
        return result.Items;
    }

    public async Task<DiskScanResult> ScanDetailedAsync(
        IEnumerable<DiskCandidate> candidates,
        IProgress<DiskScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var source = candidates.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var items = new List<FolderSizeInfo>(source.Count);
        var issues = new List<DiskScanIssue>();
        bool canceled = false;
        DiskSpaceSummary? space = null;

        try
        {
            progress?.Report(new DiskScanProgress("准备扫描", null, 0, source.Count));
            space = GetSpaceSummary();
        }
        catch (Exception ex)
        {
            issues.Add(new DiskScanIssue("C:\\", $"无法读取 C 盘空间：{ex.Message}"));
        }

        for (int i = 0; i < source.Count; i++)
        {
            var candidate = source[i];
            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new DiskScanProgress("正在测量", candidate.Path, i, source.Count));
                var measured = await MeasureAsync(candidate.Path, candidate.Category, ct);
                items.Add(measured with
                {
                    CanClean = candidate.CanClean,
                    CanMigrate = candidate.CanMigrate,
                });
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                break;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
            {
                issues.Add(new DiskScanIssue(candidate.Path, DescribeScanError(ex), ex is UnauthorizedAccessException));
                items.Add(new FolderSizeInfo(candidate.Path, candidate.Category, 0, 0)
                {
                    CanClean = candidate.CanClean,
                    CanMigrate = candidate.CanMigrate,
                    IsPartial = true,
                    SkippedCount = 1,
                    SkipReason = DescribeScanError(ex),
                });
            }
        }

        progress?.Report(new DiskScanProgress(canceled ? "扫描已取消" : "扫描完成", null,
            items.Count, source.Count, true));
        return new DiskScanResult(items, issues, space, canceled, started, DateTimeOffset.Now);
    }

    private static string DescribeScanError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有访问权限，已跳过",
        PathTooLongException => "路径过长，已跳过",
        IOException => "文件在扫描期间不可用，已跳过",
        _ => "访问失败，已跳过"
    };

    private static DiskSpaceSummary GetSpaceSummary()
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
        if (string.IsNullOrWhiteSpace(systemRoot)) systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(systemRoot)) throw new IOException("找不到系统盘");
        var drive = new DriveInfo(systemRoot);
        if (!drive.IsReady) throw new IOException("系统盘不可用");
        var total = drive.TotalSize;
        var free = drive.AvailableFreeSpace;
        return new DiskSpaceSummary(drive.Name, total, Math.Max(0, total - free), free, DateTimeOffset.Now);
    }

    private static readonly EnumerationOptions ScanOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
    };

    /// <summary>枚举钩子:测试可模拟 MoveNext 期间异常。</summary>
    protected virtual IEnumerable<string> EnumerateFiles(string dir)
        => Directory.EnumerateFiles(dir, "*", ScanOptions);

    /// <summary>枚举钩子:测试可模拟 MoveNext 期间异常。</summary>
    protected virtual IEnumerable<string> EnumerateDirectories(string dir)
        => Directory.EnumerateDirectories(dir, "*", ScanOptions);

    private FolderSizeInfo Measure(string path, DiskCategory category, CancellationToken ct)
    {
        var normalized = PathSafetyService.Normalize(path) ?? path;
        long size = 0;
        int fileCount = 0;
        int dirCount = 0;
        int skipped = 0;
        DateTimeOffset? lastWrite = null;
        bool partial = false;
        if (!Directory.Exists(normalized))
            return new FolderSizeInfo(normalized, category, 0, 0);

        var stack = new Stack<string>();
        stack.Push(normalized);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            dirCount++;

            IEnumerable<string> files;
            try { files = EnumerateFiles(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
            { skipped++; partial = true; continue; }
            try
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(f);
                        size = checked(size + info.Length);
                        fileCount++;
                        var write = info.LastWriteTimeUtc;
                        if (lastWrite is null || write > lastWrite.Value.UtcDateTime) lastWrite = write;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
                    { skipped++; partial = true; }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
            { skipped++; partial = true; }

            IEnumerable<string> subDirs;
            try { subDirs = EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
            { skipped++; partial = true; continue; }
            try
            {
                foreach (var d in subDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                        { skipped++; partial = true; continue; }
                        stack.Push(d);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
                    { skipped++; partial = true; }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
            { skipped++; partial = true; }
        }

        return new FolderSizeInfo(normalized, category, size, fileCount)
        {
            DirectoryCount = dirCount,
            LastWriteTimeUtc = lastWrite,
            IsPartial = partial,
            SkippedCount = skipped,
            SkipReason = partial ? "部分文件或目录无法访问，已跳过" : null,
        };
    }
}
