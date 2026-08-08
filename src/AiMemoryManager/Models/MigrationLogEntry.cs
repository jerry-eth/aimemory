namespace AiMemoryManager.Models;

/// <summary>
/// 迁移日志条目(FR-12.4):记录一次文件夹跨盘迁移,支持一键回退。
/// Junction 为原位置重建的联接点路径(通常等于 Source)。
/// </summary>
public record MigrationLogEntry(DateTimeOffset Time, string Source, string Target, string Junction, bool Reverted);
