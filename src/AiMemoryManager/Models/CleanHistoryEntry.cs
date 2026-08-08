namespace AiMemoryManager.Models;

public record CleanHistoryEntry(DateTimeOffset Time, CleanLevel Level, long FreedBytes, int ProcessCount, CleanTrigger Trigger);
