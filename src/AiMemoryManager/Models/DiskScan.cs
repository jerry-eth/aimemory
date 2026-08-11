namespace AiMemoryManager.Models;

public enum DiskCategory { Temp, BrowserCache, RecycleBin, UserFolder, Other }

public enum DiskAdviceSource { LocalRules, Llm, LocalFallback }

public enum DiskOperationStatus { Succeeded, Skipped, Failed }

/// <summary>清理候选项。Source/默认动作仅供展示，执行端仍须重新验证。</summary>
public record DiskCandidate(string Path, DiskCategory Category)
{
    public string Source { get; init; } = "Local";
    public bool CanClean { get; init; }
    public bool CanMigrate { get; init; }
}

/// <summary>单个候选的测量结果。</summary>
public record FolderSizeInfo(string Path, DiskCategory Category, long SizeBytes, int FileCount)
{
    public int DirectoryCount { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }
    public bool IsPartial { get; init; }
    public int SkippedCount { get; init; }
    public string? SkipReason { get; init; }
    public bool CanClean { get; init; }
    public bool CanMigrate { get; init; }
}

public sealed record DiskScanIssue(string Path, string Reason, bool IsPermissionRelated = false);

public sealed record DiskSpaceSummary(
    string Drive,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    DateTimeOffset RefreshedAt)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100d / TotalBytes;
    public string Pressure => FreeBytes < 10L * 1024 * 1024 * 1024 ? "High" :
                              FreeBytes < 30L * 1024 * 1024 * 1024 ? "Medium" : "Normal";
}

public sealed record DiskScanProgress(
    string Stage,
    string? CurrentPath,
    int Completed,
    int Total,
    bool IsCancellable = true)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}

public sealed record DiskScanResult(
    IReadOnlyList<FolderSizeInfo> Items,
    IReadOnlyList<DiskScanIssue> Issues,
    DiskSpaceSummary? Space,
    bool IsCanceled,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

public sealed record DiskOperationItemResult(
    string Path,
    DiskOperationStatus Status,
    long Bytes,
    string Message);

public sealed record DiskOperationReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<DiskOperationItemResult> Items)
{
    public int SucceededCount => Items.Count(i => i.Status == DiskOperationStatus.Succeeded);
    public int SkippedCount => Items.Count(i => i.Status == DiskOperationStatus.Skipped);
    public int FailedCount => Items.Count(i => i.Status == DiskOperationStatus.Failed);
    public long SucceededBytes => Items.Where(i => i.Status == DiskOperationStatus.Succeeded).Sum(i => i.Bytes);
}
