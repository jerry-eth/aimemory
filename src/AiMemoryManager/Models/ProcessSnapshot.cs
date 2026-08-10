namespace AiMemoryManager.Models;

/// <summary>
/// A point-in-time process sample. TotalProcessorTime is cumulative and is used
/// by the process page to calculate CPU usage between two samples.
/// </summary>
public record ProcessSnapshot(
    int Pid,
    string Name,
    string? Path,
    long WorkingSetBytes,
    bool HasVisibleWindow,
    TimeSpan TotalProcessorTime = default);
