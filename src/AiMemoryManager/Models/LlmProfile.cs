namespace AiMemoryManager.Models;

// ApiKey 永远只存密文(DPAPI Base64),任何情况下不落盘明文
public record LlmProfile
{
    public required string Id { get; init; }              // Guid "N"
    public required string Name { get; set; }
    public required string BaseUrl { get; set; }          // 如 https://api.deepseek.com/v1 或 http://localhost:11434/v1
    public string EncryptedApiKey { get; set; } = "";     // DPAPI Base64,本地模型可为空
    public required string Model { get; set; }
    public double PricePerMillionTokens { get; set; } = 0; // 0 = 不估算费用
}
