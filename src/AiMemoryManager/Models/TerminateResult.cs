namespace AiMemoryManager.Models;

// FR-2.3 L3 执行端:批量终止结果(按快照工作集估算释放量)与逐项结果(含 Win32 错误码)
public record TerminateResult(long FreedBytes, IReadOnlyList<TerminateItemResult> Items);

public record TerminateItemResult(int Pid, string Name, bool Success, int Win32Error);
