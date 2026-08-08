namespace AiMemoryManager.Models;

/// <summary>大模型对白名单候选进程的分析结果。结果仅用于建议，必须由用户确认后才会写入白名单。</summary>
public sealed record WhitelistAdvice(
    string ProcessName,
    string? Path,
    long WorkingSetBytes,
    bool Recommended,
    string Reason);

public sealed record WhitelistAdviceResult(
    IReadOnlyList<WhitelistAdvice> Suggestions,
    LlmUsage Usage,
    string ModelUsed);
