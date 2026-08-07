using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public static class AnalysisPromptBuilder
{
    public const int MaxProcesses = 30;
    private const long BucketBytes = 32L << 20;   // 32MB 桶,吸收内存抖动

    public static string BuildUserPrompt(
        IReadOnlyList<ProcessSnapshot> snapshots, SystemMemoryInfo memory,
        string customInstructions, string language)
    {
        var top = snapshots
            .OrderByDescending(p => p.WorkingSetBytes)
            .Take(MaxProcesses)
            .Select(p => new { name = p.Name, memoryMB = p.WorkingSetBytes >> 20, path = p.Path ?? "" });
        string json = JsonSerializer.Serialize(top);
        string mem = $"已用 {memory.UsedPercent:F0}%({(memory.TotalBytes - memory.AvailableBytes) >> 30}GB / {memory.TotalBytes >> 30}GB)";
        string custom = string.IsNullOrWhiteSpace(customInstructions) ? "(none 无)" : customInstructions;

        return $$"""
        当前系统内存状况:{{mem}}。
        内存占用最高的进程列表(JSON):
        {{json}}

        用户附加要求:{{custom}}

        请分析并给出值得处理的进程建议,只输出 JSON:
        {"suggestions":[{"process":"进程名","action":"compress|terminate|keep","reason":"一句话理由","risk":"low|medium|high"}]}
        规则:compress=可回收工作集;terminate=建议用户关闭;keep=保留;拿不准一律 keep;系统关键进程不要出现。reason 用{{language}}书写,不超过 30 字。
        """;
    }

    public static string SnapshotHash(IReadOnlyList<ProcessSnapshot> snapshots, string model, string templateContent)
    {
        var sb = new StringBuilder(model).Append('|').Append(templateContent).Append('|');
        foreach (var p in snapshots.OrderByDescending(p => p.WorkingSetBytes).Take(MaxProcesses))
            sb.Append(p.Name).Append(':').Append(p.WorkingSetBytes / BucketBytes).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
