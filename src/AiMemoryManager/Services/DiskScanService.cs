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
            try
            {
                list.Add(await MeasureAsync(c.Path, c.Category, ct));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
            {
                // 单个候选失败不致命:跳过,继续测量其余候选
            }
        }
        return list;
    }

    private static readonly EnumerationOptions ScanOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,   // 不按属性过滤,保持与原先无选项枚举一致的口径
        RecurseSubdirectories = false,
    };

    /// <summary>枚举钩子:可重写以模拟枚举期异常(惰性枚举的异常发生在 MoveNext)。</summary>
    protected virtual IEnumerable<string> EnumerateFiles(string dir)
        => Directory.EnumerateFiles(dir, "*", ScanOptions);

    /// <summary>枚举钩子:可重写以模拟枚举期异常(惰性枚举的异常发生在 MoveNext)。</summary>
    protected virtual IEnumerable<string> EnumerateDirectories(string dir)
        => Directory.EnumerateDirectories(dir, "*", ScanOptions);

    private FolderSizeInfo Measure(string path, DiskCategory category, CancellationToken ct)
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

            // 惰性枚举:IgnoreInaccessible 跳过无权限项;try 包住整个 foreach,
            // 因为异常实际发生在 MoveNext(如扫描中途目录被删),仅包住调用点无效。
            IEnumerable<string> files;
            try { files = EnumerateFiles(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { continue; }

            try
            {
                foreach (var f in files)
                {
                    try
                    {
                        size += new FileInfo(f).Length;
                        count++;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { /* 跳过单文件 */ }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { /* 枚举中途失败,继续子目录 */ }

            IEnumerable<string> subDirs;
            try { subDirs = EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { continue; }

            try
            {
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
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { /* 枚举中途失败,继续栈中剩余目录 */ }
        }
        return new FolderSizeInfo(path, category, size, count);
    }
}
