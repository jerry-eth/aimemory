namespace AiMemoryManager.Models;

public enum AnalysisTrigger { Manual, Threshold, Timer, Leak, Conversation }

public record AnalysisSuggestion(string ProcessName, string Action, string Reason, string Risk); // Action: "compress"|"terminate"|"keep";Risk: "low"|"medium"|"high"

public record AnalysisResult(DateTimeOffset Time, IReadOnlyList<AnalysisSuggestion> Suggestions,
    LlmUsage Usage, string ModelUsed, bool FromCache, AnalysisTrigger Trigger);

/// <summary>面向用户展示的智能分析报告，和建议列表一起保留在共享 VM 中。</summary>
public record AnalysisReport(
    DateTimeOffset Time,
    string ModelUsed,
    bool FromCache,
    int ProcessCount,
    int SuggestionCount,
    double UsedPercent,
    string Summary,
    IReadOnlyList<string> Recommendations)
{
    public string MemoryText => $"{UsedPercent:F0}%";
}

public record AnalysisChatMessage(string Role, string Content, DateTimeOffset Time)
{
    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);
    public bool IsAssistant => !IsUser;
}

public record AnalysisActionPlan(string Operation, IReadOnlyList<string> Targets, string Reason, string Risk)
{
    public bool IsExecutable => Operation is "clean_working_sets" or "purge_standby" or "terminate_processes";
}

public record AnalysisChatResponse(string Answer, AnalysisActionPlan? Plan, LlmUsage Usage);
