using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class LlmIntegrationTests
{
    private static string? Key => Environment.GetEnvironmentVariable("AMM_TEST_LLM_KEY");
    private static string BaseUrl => Environment.GetEnvironmentVariable("AMM_TEST_LLM_URL") ?? "https://api.deepseek.com/v1";
    private static string Model => Environment.GetEnvironmentVariable("AMM_TEST_LLM_MODEL") ?? "deepseek-v4-flash";

    [Fact]
    public async Task 真实端点_ListModels可用()
    {
        if (string.IsNullOrEmpty(Key)) return;   // 无密钥环境自动跳过
        var client = new OpenAiCompatibleClient();
        var profile = new LlmProfile
        {
            Id = "t", Name = "t", BaseUrl = BaseUrl,
            EncryptedApiKey = SecretProtector.Protect(Key!), Model = Model
        };
        var models = await client.ListModelsAsync(profile);
        Assert.NotEmpty(models);
    }

    [Fact]
    public async Task 真实端点_分析输出可解析()
    {
        if (string.IsNullOrEmpty(Key)) return;
        var client = new OpenAiCompatibleClient();
        var profile = new LlmProfile
        {
            Id = "t", Name = "t", BaseUrl = BaseUrl,
            EncryptedApiKey = SecretProtector.Protect(Key!), Model = Model
        };
        var snapshots = new[]
        {
            new ProcessSnapshot(1, "chrome", @"C:\chrome.exe", 900L << 20, true),
            new ProcessSnapshot(2, "notepad", @"C:\notepad.exe", 50L << 20, true),
        };
        var prompt = AnalysisPromptBuilder.BuildUserPrompt(
            snapshots, new SystemMemoryInfo(16L << 30, 4L << 30), "", "中文");
        var resp = await client.ChatAsync(profile, PromptTemplateService.BuiltinContent, prompt);
        Assert.True(resp.Usage.InputTokens > 0);
        var suggestions = AnalysisResultParser.Parse(resp.Content);
        Assert.NotNull(suggestions);   // 解析不抛;真实模型应给出合法 JSON
    }
}
