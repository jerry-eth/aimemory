namespace AiMemoryManager.Models;

/// <summary>可安全清理项(FR-12.2):路径 + 理由 + 估计可释放字节数。</summary>
public record DiskCleanableItem(string Path, string Reason, long EstBytes);

/// <summary>适合迁移项(FR-12.2):路径 + 理由 + 目标盘符(如 "D:")。</summary>
public record DiskMigratableItem(string Path, string Reason, string TargetDrive);

/// <summary>大模型磁盘瘦身建议:可安全清理项 + 适合迁移项。</summary>
public record DiskAdvice(IReadOnlyList<DiskCleanableItem> Cleanable, IReadOnlyList<DiskMigratableItem> Migratable);
