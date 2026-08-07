namespace AiMemoryManager.Models;

public enum AnalysisTrigger { Manual, Threshold, Timer, Leak }

public record AnalysisSuggestion(string ProcessName, string Action, string Reason, string Risk); // Action: "compress"|"terminate"|"keep";Risk: "low"|"medium"|"high"

public record AnalysisResult(DateTimeOffset Time, IReadOnlyList<AnalysisSuggestion> Suggestions,
    LlmUsage Usage, string ModelUsed, bool FromCache, AnalysisTrigger Trigger);
