using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace AiMemoryManager.Services;

/// <summary>
/// 删除到回收站(FR-12.3):仅走 SendToRecycleBin,永不永久删除。
/// 执行端强制重查 SystemPathGuard:受保护路径一律拒绝,不依赖 UI/LLM 过滤。
/// </summary>
public class RecycleBinDeleteService
{
    public bool DeleteDirectoryToRecycleBin(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            if (SystemPathGuard.IsProtected(path)) return false;   // 执行端强制重查(FR-12.5)
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch { return false; }
    }

    public bool DeleteFileToRecycleBin(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (SystemPathGuard.IsProtected(path)) return false;   // 执行端强制重查(FR-12.5)
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch { return false; }
    }
}
