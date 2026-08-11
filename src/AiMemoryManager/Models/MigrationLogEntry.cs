namespace AiMemoryManager.Models;

public enum MigrationState
{
    Copying,
    Copied,
    SourceStaged,
    Linked,
    Completed,
    Reverting,
    Reverted,
    Failed
}

/// <summary>
/// 迁移日志条目。保留旧版五参数构造和 Reverted 属性，新增字段用于中断恢复。
/// </summary>
public record MigrationLogEntry(DateTimeOffset Time, string Source, string Target, string Junction, bool Reverted)
{
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");
    public string? BackupPath { get; init; }
    public MigrationState State { get; init; } = Reverted ? MigrationState.Reverted : MigrationState.Completed;
    public long SourceBytes { get; init; }
    public int SourceFileCount { get; init; }
    public long TargetBytes { get; init; }
    public int TargetFileCount { get; init; }
    public string? Error { get; init; }
    public bool IsRecoverable => !Reverted && State != MigrationState.Completed;
}
