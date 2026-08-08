namespace AiMemoryManager.Models;

public enum CleanLevel { L1, L2, L3 }

public enum CleanTrigger { Manual, RuleThreshold, RuleTimer, Tray, Analysis, Conversation }

public record CleanResult(DateTimeOffset Time, CleanLevel Level, long FreedBytes, int ProcessCount, CleanTrigger Trigger);
