using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class AnalysisChatServiceTests
{
    [Fact]
    public void 解析回答和可执行计划()
    {
        var response = AnalysisChatService.Parse("""{"answer":"可以清理","plan":{"operation":"terminate","targets":["game.exe"],"reason":"后台高占用","risk":"medium"}}""", new LlmUsage(1, 2));
        Assert.Equal("可以清理", response.Answer);
        Assert.NotNull(response.Plan);
        Assert.Equal("terminate_processes", response.Plan!.Operation);
        Assert.Equal("game", response.Plan.Targets.Single());
        Assert.True(response.Plan.IsExecutable);
    }

    [Fact]
    public void 坏输出不得生成执行计划()
    {
        var response = AnalysisChatService.Parse("这不是 JSON", new LlmUsage(0, 0));
        Assert.Null(response.Plan);
        Assert.Equal("这不是 JSON", response.Answer);
    }

    [Fact]
    public void 报告包含摘要和建议()
    {
        var result = new AnalysisResult(DateTimeOffset.UtcNow,
            new[] { new AnalysisSuggestion("chrome", "compress", "占用高", "low") },
            new LlmUsage(10, 5), "m1", false, AnalysisTrigger.Manual);
        var report = AnalysisReportBuilder.Build(result, new SystemMemoryInfo(16L << 30, 4L << 30), 12);
        Assert.Equal(12, report.ProcessCount);
        Assert.Equal(1, report.SuggestionCount);
        Assert.Contains("chrome", report.Recommendations.Single());
    }
}
