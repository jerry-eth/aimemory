namespace AiMemoryManager.Models;

public record ProcessSnapshot(int Pid, string Name, string? Path, long WorkingSetBytes, bool HasVisibleWindow);
