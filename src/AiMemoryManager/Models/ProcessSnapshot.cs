namespace AiMemoryManager.Models;

/// <summary>进程可执行文件的数字签名状态。</summary>
public enum ProcessSignatureStatus
{
    Unknown,
    Signed,
    Unsigned
}

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
    TimeSpan TotalProcessorTime = default,
    ProcessSignatureStatus SignatureStatus = ProcessSignatureStatus.Unknown);
