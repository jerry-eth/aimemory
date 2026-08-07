using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public interface ILlmClient
{
    Task<LlmResponse> ChatAsync(LlmProfile profile, string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListModelsAsync(LlmProfile profile, CancellationToken ct = default);
}
