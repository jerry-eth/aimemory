namespace AiMemoryManager.Models;

public record AnalysisActionExecutionResult(string Operation, long FreedBytes, int ProcessCount, int FailedCount);
