using System.IO;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>
/// 磁盘扫描(FR-12.1):迭代栈遍历测量候选目录大小。
/// 跳过 reparse point / junction 防循环;无权限路径跳过而非失败。
/// </summary>
public class DiskScanService
{
    public Task<FolderSizeInfo> MeasureAsync(string path, DiskCategory category, CancellationToken ct = default)
        => Task.Run(() => Measure(path, category, ct), ct);

    /// <summary>批量测量,串行执行避免磁盘 IO 抖动;单个失败不影响其它项。</summary>
    public async Task<IReadOnlyList<FolderSizeInfo>> ScanAsync(IEnumerable<DiskCandidate> candidates, CancellationToken ct = default)
    {
        var list = new List<FolderSizeInfo>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(await MeasureAsync(c.Path, c.Category, ct));
        }
        return list;
    }

    private static FolderSizeInfo Measure(string path, DiskCategory category, CancellationToken ct)
    {
        long size = 0;
        int count = 0;
        if (!Directory.Exists(path)) return new FolderSizeInfo(path, category, 0, 0);

        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { continue; }

            foreach (var f in files)
            {
                try
                {
                    size += new FileInfo(f).Length;
                    count++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { /* 跳过单文件 */ }
            }

            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { continue; }

            foreach (var d in subDirs)
            {
                try
                {
                    // 跳过符号链接/联接点,避免循环与重复计量
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push(d);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { /* 跳过 */ }
            }
        }
        return new FolderSizeInfo(path, category, size, count);
    }
}
