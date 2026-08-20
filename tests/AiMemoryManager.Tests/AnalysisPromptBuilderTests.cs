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
        Assert.Equal(AnalysisPromptBuilder.SnapshotHash(a, "m", "t", "", "中文"), AnalysisPromptBuilder.SnapshotHash(b, "m", "t", "", "中文"));
        Assert.NotEqual(AnalysisPromptBuilder.SnapshotHash(a, "m", "t", "", "中文"), AnalysisPromptBuilder.SnapshotHash(c, "m", "t", "", "中文"));
        Assert.NotEqual(AnalysisPromptBuilder.SnapshotHash(a, "m", "t", "", "中文"), AnalysisPromptBuilder.SnapshotHash(a, "m2", "t", "", "中文"));
    }

    [Fact] public void 快照哈希对同名进程顺序互换稳定()
    {
        // 同名多实例内存相近时,两次采样的降序排列可能互换;哈希不应因此变化(否则真实系统上缓存永不命中)
        var a = new List<ProcessSnapshot>
        {
            new(1, "chrome", null, 500L << 20, true),
            new(2, "chrome", null, 498L << 20, true),
            new(3, "code", null, 300L << 20, true),
        };
        var b = new List<ProcessSnapshot>
        {
            new(2, "chrome", null, 498L << 20, true),
            new(1, "chrome", null, 500L << 20, true),
            new(3, "code", null, 300L << 20, true),
        };
        Assert.Equal(AnalysisPromptBuilder.SnapshotHash(a, "m", "t", "", "中文"),
                     AnalysisPromptBuilder.SnapshotHash(b, "m", "t", "", "中文"));
    }

    [Fact] public void 快照哈希纳入自定义指令与语言()
    {
        var a = Snapshots(3);
        Assert.NotEqual(AnalysisPromptBuilder.SnapshotHash(a, "m", "t", "别动游戏", "中文"),
                        AnalysisPromptBuilder.SnapshotHash(a, "m", "t", "", "中文"));
        Assert.NotEqual(AnalysisPromptBuilder.SnapshotHash(a, "m", "t", "", "中文"),
                        AnalysisPromptBuilder.SnapshotHash(a, "m", "t", "", "English"));
    }

    [Fact] public void 模板占位符被替换为实际片段()
    {
        var f = AnalysisPromptBuilder.BuildFragments(
            Snapshots(2), new SystemMemoryInfo(16L << 30, 4L << 30), "别动我的游戏", "中文");
        var rendered = AnalysisPromptBuilder.RenderTemplate(
            "mem={memory_info}; procs={process_list}; custom={custom_instructions}; lang={language}", f);
        Assert.DoesNotContain("{memory_info}", rendered);
        Assert.DoesNotContain("{process_list}", rendered);
        Assert.DoesNotContain("{custom_instructions}", rendered);
        Assert.DoesNotContain("{language}", rendered);
        Assert.Contains("75", rendered);                 // 内存信息
        Assert.Contains("\"name\":\"proc2\"", rendered); // 进程 JSON
        Assert.Contains("别动我的游戏", rendered);
        Assert.Contains("lang=中文", rendered);
    }

    [Fact] public void 片段与用户提示词内容一致()
    {
        var f = AnalysisPromptBuilder.BuildFragments(
            Snapshots(2), new SystemMemoryInfo(16L << 30, 4L << 30), "x", "中文");
        var prompt = AnalysisPromptBuilder.BuildUserPrompt(
            Snapshots(2), new SystemMemoryInfo(16L << 30, 4L << 30), "x", "中文");
        Assert.Contains(f.MemoryInfo, prompt);
        Assert.Contains(f.ProcessListJson, prompt);
    }
}
