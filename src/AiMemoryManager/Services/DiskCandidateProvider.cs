using System.IO;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>
/// 清理候选清单(FR-12.1):Temp、浏览器缓存、回收站、用户目录一级文件夹。
/// 所有候选先经 SystemPathGuard 硬白名单过滤;Windows Update 缓存受 FR-12.5 保护,不纳入。
/// </summary>
public class DiskCandidateProvider
{
    public IReadOnlyList<DiskCandidate> GetCandidates()
    {
        var list = new List<DiskCandidate>();

        // %TEMP%
        AddIfExists(list, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), DiskCategory.Temp);

        // 浏览器缓存:Chrome / Edge
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddIfExists(list, Path.Combine(local, @"Google\Chrome\User Data\Default\Cache"), DiskCategory.BrowserCache);
        AddIfExists(list, Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache"), DiskCategory.BrowserCache);

        // 回收站:访问失败时 MeasureAsync 自然得 0,UI 可忽略 0 项
        string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!;
        Add(list, Path.Combine(sysRoot, "$Recycle.Bin"), DiskCategory.RecycleBin);

        // %USERPROFILE% 一级目录(Downloads/Documents/Desktop/Pictures/Videos/Music/AppData 等),
        // 只列存在者,大小由 DiskScanService 统一测量,UI 量完按大小排序展示 Top15
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> firstLevel;
        try { firstLevel = Directory.EnumerateDirectories(profile); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { firstLevel = Enumerable.Empty<string>(); }
        foreach (var d in firstLevel)
        {
            try
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;   // 跳过 junction(如 "Application Data")
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
            Add(list, d, DiskCategory.UserFolder);
        }

        return list;
    }

    private static void AddIfExists(List<DiskCandidate> list, string path, DiskCategory category)
    {
        if (Directory.Exists(path)) Add(list, path, category);
    }

    private static void Add(List<DiskCandidate> list, string path, DiskCategory category)
    {
        if (SystemPathGuard.IsProtected(path)) return;   // 硬白名单兜底
        list.Add(new DiskCandidate(path, category));
    }
}
