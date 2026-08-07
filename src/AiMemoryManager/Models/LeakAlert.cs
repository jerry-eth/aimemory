namespace AiMemoryManager.Models;

public record LeakAlert(int Pid, string ProcessName, long GrowthBytes, TimeSpan Window, DateTimeOffset Time);
