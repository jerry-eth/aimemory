using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class AnalysisPromptBuilderTests
{
    private static List<ProcessSnapshot> Snapshots(int n) => Enumerable.Range(1, n)
        .Select(i => new ProcessSnapshot(i, $"proc{i}", $@"C:\apps\proc{i}.exe", i * (100L << 20), true))
        .ToList();

    [Fact] public void 最多取30个且按内存降序()
    {
        var prompt = AnalysisPromptBuilder.BuildUserPrompt(
            Snapshots(40), new SystemMemoryInfo(16L << 30, 4L << 30), "", "中文");
        Assert.Contains("proc40", prompt);          // 最大内存的在内
        Assert.DoesNotContain("proc1,", prompt);    // 最小的被截掉(proc1 不在 JSON 中)
        Assert.DoesNotContain("\"name\":\"proc10\"", prompt);
    }

    [Fact] public void 包含内存信息与自定义指令和占位替换()
    {
        var prompt = AnalysisPromptBuilder.BuildUserPrompt(
            Snapshots(2), new SystemMemoryInfo(16L << 30, 4L << 30), "别动我的游戏", "中文");
        Assert.Contains("75", prompt);              // 占用率 75%
        Assert.Contains("别动我的游戏", prompt);
        Assert.DoesNotContain("{process_list}", prompt);
    }

    [Fact] public void 空自定义指令显示为无()
    {
        var prompt = AnalysisPromptBuilder.BuildUserPrompt(
            Snapshots(1), new SystemMemoryInfo(1000, 500), "", "English");
        Assert.Contains("none", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void 快照哈希对同名单同桶内存稳定_对变化敏感()
    {
        var a = Snapshots(3);
        var b = Snapshots(3).Select(p => p with { WorkingSetBytes = p.WorkingSetBytes + (1L << 20) }).ToList(); // +1MB 同桶
        var c = Snapshots(3).Select(p => p with { WorkingSetBytes = p.WorkingSetBytes + (64L << 20) }).ToList(); // +64MB 跨桶
        Assert.Equal(AnalysisPromptBuilder.SnapshotHash(a, "m", "t"), AnalysisPromptBuilder.SnapshotHash(b, "m", "t"));
        Assert.NotEqual(AnalysisPromptBuilder.SnapshotHash(a, "m", "t"), AnalysisPromptBuilder.SnapshotHash(c, "m", "t"));
        Assert.NotEqual(AnalysisPromptBuilder.SnapshotHash(a, "m", "t"), AnalysisPromptBuilder.SnapshotHash(a, "m2", "t"));
    }
}
