using System.IO;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>发现默认安全候选，不把用户目录一级目录直接当作可删除项。</summary>
public class DiskCandidateProvider
{
    public IReadOnlyList<DiskCandidate> GetCandidates()
    {
        var list = new List<DiskCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 临时目录按一级子目录拆分，避免把正在使用的整个 TEMP 根目录移入回收站。
        AddTempChildren(list, seen, Path.GetTempPath());

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddIfExists(list, seen, Path.Combine(local, @"Google\Chrome\User Data\Default\Cache"), DiskCategory.BrowserCache, canClean: true);
        AddIfExists(list, seen, Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache"), DiskCategory.BrowserCache, canClean: true);
        AddIfExists(list, seen, Path.Combine(local, @"Mozilla\Firefox\Profiles"), DiskCategory.BrowserCache, canClean: true);

        // 回收站只作为统计分类，不对 $Recycle.Bin 目录本身执行删除。
        var sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
        if (!string.IsNullOrEmpty(sysRoot))
            AddIfExists(list, seen, Path.Combine(sysRoot, "$Recycle.Bin"), DiskCategory.RecycleBin, canClean: false);

        // 用户数据目录只作为迁移候选，禁止默认清理；跳过 AppData、链接和当前应用目录。
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "Downloads", "Documents", "Desktop", "Pictures", "Videos", "Music" })
        {
            var path = Path.Combine(profile, name);
            AddIfExists(list, seen, path, DiskCategory.UserFolder, canMigrate: true);
        }
        AddOptionalUserFolders(list, seen, profile);
        return list;
    }

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
    };

    private static void AddTempChildren(List<DiskCandidate> list, HashSet<string> seen, string tempRoot)
    {
        if (!Directory.Exists(tempRoot)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(tempRoot, "*", Options))
                AddIfExists(list, seen, dir, DiskCategory.Temp, canClean: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { }
    }

    private static void AddOptionalUserFolders(List<DiskCandidate> list, HashSet<string> seen, string profile)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(profile, "*", Options))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, "AppData", StringComparison.OrdinalIgnoreCase) ||
                    name is "Downloads" or "Documents" or "Desktop" or "Pictures" or "Videos" or "Music") continue;
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                    if (SystemPathGuard.IsProtected(dir)) continue;
                    AddIfExists(list, seen, dir, DiskCategory.UserFolder, canMigrate: true);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException) { }
    }

    private static void AddIfExists(List<DiskCandidate> list, HashSet<string> seen, string path,
        DiskCategory category, bool canClean = false, bool canMigrate = false)
    {
        if (!Directory.Exists(path)) return;
        var full = PathSafetyService.Normalize(path);
        if (full is null || !seen.Add(full)) return;
        if (PathSafetyService.IsReparsePoint(full)) return;
        if (SystemPathGuard.IsProtected(full)) return;
        if (canClean && !PathSafetyService.IsSafeCleanCandidate(full, category)) return;
        if (canMigrate && !PathSafetyService.IsSafeMigrationCandidate(full, category)) return;
        list.Add(new DiskCandidate(full, category)
        {
            Source = "LocalRules",
            CanClean = canClean,
            CanMigrate = canMigrate,
        });
    }
}
