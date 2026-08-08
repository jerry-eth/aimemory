namespace AiMemoryManager.Models;

public enum DiskCategory { Temp, BrowserCache, RecycleBin, UserFolder, Other }

/// <summary>磁盘清理候选项(FR-12.1):路径 + 分类,大小由 DiskScanService 统一测量。</summary>
public record DiskCandidate(string Path, DiskCategory Category);

/// <summary>单个候选的测量结果。</summary>
public record FolderSizeInfo(string Path, DiskCategory Category, long SizeBytes, int FileCount);
