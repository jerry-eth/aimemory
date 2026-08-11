using System.IO;
using AiMemoryManager.Models;
using Microsoft.VisualBasic.FileIO;

namespace AiMemoryManager.Services;

/// <summary>只使用 Windows 回收站 API 的清理服务，永不以永久删除作为降级方案。</summary>
public class RecycleBinDeleteService
{
    public bool DeleteDirectoryToRecycleBin(string path) =>
        DeleteDirectoryToRecycleBin(path, DiskCategory.Temp);

    public bool DeleteDirectoryToRecycleBin(string path, DiskCategory category)
    {
        try
        {
            var full = PathSafetyService.Normalize(path);
            if (full is null || !Directory.Exists(full)) return false;
            if (PathSafetyService.IsProtectedForOperation(full, category, migration: false)) return false;
            FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return !Directory.Exists(full);
        }
        catch { return false; }
    }

    public bool DeleteFileToRecycleBin(string path) =>
        DeleteFileToRecycleBin(path, DiskCategory.Temp);

    public bool DeleteFileToRecycleBin(string path, DiskCategory category)
    {
        try
        {
            var full = PathSafetyService.Normalize(path);
            if (full is null || !File.Exists(full)) return false;
            if (PathSafetyService.IsProtectedForOperation(full, category, migration: false)) return false;
            FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return !File.Exists(full);
        }
        catch { return false; }
    }

    public Task<DiskOperationReport> DeleteDirectoriesAsync(
        IEnumerable<(string Path, DiskCategory Category)> items,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            var started = DateTimeOffset.Now;
            var results = new List<DiskOperationItemResult>();
            foreach (var item in items.DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                long bytes = 0;
                try
                {
                    var full = PathSafetyService.Normalize(item.Path);
                    if (full is null || !Directory.Exists(full))
                    {
                        results.Add(new(item.Path, DiskOperationStatus.Skipped, 0, "路径不存在或已被处理"));
                        continue;
                    }
                    bytes = DirectorySizeEstimator.Estimate(full);
                    if (PathSafetyService.IsProtectedForOperation(full, item.Category, migration: false))
                    {
                        results.Add(new(full, DiskOperationStatus.Skipped, bytes, "路径受安全策略保护"));
                        continue;
                    }
                    if (DeleteDirectoryToRecycleBin(full, item.Category))
                        results.Add(new(full, DiskOperationStatus.Succeeded, bytes, "已移入回收站"));
                    else
                        results.Add(new(full, DiskOperationStatus.Failed, 0, "无法移入回收站，原路径未强制删除"));
                }
                catch (Exception ex)
                {
                    results.Add(new(item.Path, DiskOperationStatus.Failed, 0, ex.Message));
                }
            }
            return new DiskOperationReport(started, DateTimeOffset.Now, results);
        }, ct);
}

internal static class DirectorySizeEstimator
{
    public static long Estimate(string path)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { IgnoreInaccessible = true }))
                {
                    try { total = checked(total + new FileInfo(file).Length); } catch { }
                }
                foreach (var child in Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { IgnoreInaccessible = true }))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) stack.Push(child);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return total;
    }
}
