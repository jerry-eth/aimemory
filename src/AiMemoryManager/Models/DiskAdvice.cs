namespace AiMemoryManager.Models;

/// <summary>可安全清理项。安全属性由本地规则计算，执行时仍需再次校验。</summary>
public record DiskCleanableItem(string Path, string Reason, long EstBytes)
{
    public DiskCategory Category { get; init; } = DiskCategory.Other;
    public string Risk { get; init; } = "Review";
}

/// <summary>适合迁移项。迁移目标只允许来自当前可用固定盘。</summary>
public record DiskMigratableItem(string Path, string Reason, string TargetDrive)
{
    public DiskCategory Category { get; init; } = DiskCategory.UserFolder;
    public string Risk { get; init; } = "Review";
}

/// <summary>磁盘瘦身建议。</summary>
public record DiskAdvice(IReadOnlyList<DiskCleanableItem> Cleanable, IReadOnlyList<DiskMigratableItem> Migratable)
{
    public DiskAdviceSource Source { get; init; } = DiskAdviceSource.LocalRules;
    public string? StatusMessage { get; init; }
}
