namespace AiMemoryManager.Models;

// FR-2.7 后悔药:终止前捕获的记录,用于"一键恢复"重启进程
public record KillRecord(DateTimeOffset Time, int Pid, string Name, string? Path, string? Arguments)
{
    public string Source { get; init; } = "Manual";
}
