using System.IO;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>
/// C 盘操作的执行端安全策略。UI、LLM 和旧扫描结果都不能绕过这些检查。
/// </summary>
public static class PathSafetyService
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            // Windows 的 "D:" 是当前目录语义而不是 D 盘根目录；拒绝这种
            // 模糊路径，避免把迁移目标解析到意外位置。
            if (path.Trim().Length == 2 && path.Trim()[1] == ':') return null;
            var full = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && full.Equals(root, StringComparison.OrdinalIgnoreCase))
                return root;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return null; }
    }

    public static bool IsSameOrDescendant(string path, string parent)
    {
        var child = Normalize(path);
        var root = Normalize(parent);
        if (child is null || root is null) return false;
        if (child.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var separator = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return child.StartsWith(separator, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveRoot(string path)
    {
        var full = Normalize(path);
        if (full is null) return true;
        var root = Path.GetPathRoot(full);
        return root is not null && full.Equals(root, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    public static bool IsProtectedForOperation(string path, DiskCategory category, bool migration)
    {
        var full = Normalize(path);
        if (full is null || SystemPathGuard.IsProtected(full) || IsDriveRoot(full)) return true;
        if (IsReparsePoint(full)) return true;

        var profile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (profile is not null && full.Equals(profile, StringComparison.OrdinalIgnoreCase)) return true;

        var appBase = Normalize(AppContext.BaseDirectory);
        if (appBase is not null && IsSameOrDescendant(full, appBase)) return true;

        // AppData 根和配置目录不能整体删除/迁移；只有明确的缓存子目录可清理。
        var localAppData = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var roaming = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        if ((localAppData is not null && IsSameOrDescendant(full, localAppData)) ||
            (roaming is not null && IsSameOrDescendant(full, roaming)))
        {
            var tempRoot = Normalize(Path.GetTempPath());
            var isTemp = tempRoot is not null && IsSameOrDescendant(full, tempRoot);
            if (isTemp && tempRoot is not null && full.Equals(tempRoot, StringComparison.OrdinalIgnoreCase)) return true;
            if (migration) return !isTemp;
            if (category is not (DiskCategory.Temp or DiskCategory.BrowserCache)) return true;
            if (!IsKnownCachePath(full, category)) return true;
        }

        // 系统保留目录即使未被旧版 SystemPathGuard 列出也不允许操作。
        string[] protectedNames = { "WindowsApps", "Installer", "System Volume Information", "$Recycle.Bin" };
        foreach (var name in protectedNames)
        {
            var root = Path.GetPathRoot(full);
            if (root is not null && IsSameOrDescendant(full, Path.Combine(root, name))) return true;
        }
        return false;
    }

    public static bool IsKnownCachePath(string path, DiskCategory category)
    {
        var full = Normalize(path);
        if (full is null) return false;
        var temp = Normalize(Path.GetTempPath());
        if (category == DiskCategory.Temp && temp is not null && IsSameOrDescendant(full, temp)) return true;
        var local = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (local is null) return false;
        var cacheRoots = new[]
        {
            Path.Combine(local, "Google", "Chrome", "User Data"),
            Path.Combine(local, "Microsoft", "Edge", "User Data"),
            Path.Combine(local, "Mozilla", "Firefox", "Profiles"),
            Path.Combine(local, "Packages")
        };
        return cacheRoots.Any(root => IsSameOrDescendant(full, root) &&
            (category == DiskCategory.BrowserCache || full.Contains("Cache", StringComparison.OrdinalIgnoreCase) ||
             full.Contains("Temp", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsSafeCleanCandidate(string path, DiskCategory category)
        => category is DiskCategory.Temp or DiskCategory.BrowserCache &&
           !IsProtectedForOperation(path, category, migration: false);

    public static bool IsSafeMigrationCandidate(string path, DiskCategory category)
        => category == DiskCategory.UserFolder &&
           !IsProtectedForOperation(path, category, migration: true);

    public static bool IsSafeTarget(string source, string targetRoot, out string target, out string error)
    {
        target = string.Empty;
        error = string.Empty;
        var src = Normalize(source);
        var root = Normalize(targetRoot);
        if (src is null || root is null) { error = "路径格式无效"; return false; }
        if (SystemPathGuard.IsProtected(src) || IsDriveRoot(src) || IsReparsePoint(src))
        { error = "源路径受保护或为链接路径"; return false; }
        if (SystemPathGuard.IsProtected(root) || IsReparsePoint(root))
        { error = "目标路径受保护或为链接路径"; return false; }
        if (IsSameOrDescendant(root, src)) { error = "目标目录不能位于源目录内"; return false; }
        var name = Path.GetFileName(src);
        if (string.IsNullOrWhiteSpace(name)) { error = "无法确定源目录名称"; return false; }
        target = Path.Combine(root, name);
        if (IsSameOrDescendant(target, src) || IsSameOrDescendant(src, target))
        { error = "源目录和目标目录存在嵌套关系"; return false; }
        return true;
    }
}
