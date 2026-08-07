namespace AiMemoryManager.Models;

public record LlmUsage(int InputTokens, int OutputTokens);
public record LlmResponse(string Content, LlmUsage Usage);
