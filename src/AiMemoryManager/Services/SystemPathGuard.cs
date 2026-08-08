using System.IO;

namespace AiMemoryManager.Services;

/// <summary>
/// 系统路径硬白名单(FR-12.5):Windows / Program Files / Program Files (x86) / ProgramData
/// 及其子路径永远不得进入清理/迁移建议。解析失败的路径一律按受保护处理(宁可不碰)。
/// </summary>
public static class SystemPathGuard
{
    private static readonly string[] Protected =
    {
        @"Windows", @"Program Files", @"Program Files (x86)", @"ProgramData",
    };

    public static bool IsProtected(string path)
    {
        string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return true; }   // 解析不了的路径按受保护处理(宁可不碰)
        foreach (var p in Protected)
        {
            string guard = Path.Combine(root, p);
            if (full.Equals(guard, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(guard + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
