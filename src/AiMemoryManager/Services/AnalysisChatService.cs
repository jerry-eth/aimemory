using System.Text.Json;
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public static class AnalysisReportBuilder
{
    public static AnalysisReport Build(AnalysisResult result, SystemMemoryInfo memory, int processCount)
    {
        var actionable = result.Suggestions.Count(s => s.Action is "compress" or "terminate");
        var summary = result.Suggestions.Count == 0
            ? "当前快照未发现明确需要处理的进程。"
            : $"发现 {result.Suggestions.Count} 条建议，其中 {actionable} 条可进一步处理。";
        var recommendations = result.Suggestions
            .Where(s => s.Action is "compress" or "terminate")
            .Take(8)
            .Select(s => $"{s.ProcessName}：{s.Reason}")
            .ToList();
        return new AnalysisReport(result.Time, result.ModelUsed, result.FromCache,
            processCount, result.Suggestions.Count, memory.UsedPercent, summary, recommendations);
    }
}

public sealed class AnalysisChatService
{
    private readonly ILlmClient _client;
    private readonly LlmProfileService _profiles;
    private readonly INativeMemoryApi _native;
    private readonly WhitelistService _whitelist;
    private readonly ForegroundGuard _guard;
    private readonly TokenStatsService _stats;
    private readonly LocalizationService _l10n;

    public AnalysisChatService(ILlmClient client, LlmProfileService profiles, INativeMemoryApi native,
        WhitelistService whitelist, ForegroundGuard guard, TokenStatsService stats, LocalizationService l10n)
        => (_client, _profiles, _native, _whitelist, _guard, _stats, _l10n)
         = (client, profiles, native, whitelist, guard, stats, l10n);

    public async Task<AnalysisChatResponse> ChatAsync(AnalysisReport? report,
        IReadOnlyList<AnalysisChatMessage> history, string userMessage, CancellationToken ct = default)
    {
        var profile = _profiles.GetActive()
            ?? throw new InvalidOperationException("尚未配置大模型档案,请先在“大模型”页添加");
        if (string.IsNullOrWhiteSpace(userMessage))
            throw new ArgumentException("请输入问题或操作要求", nameof(userMessage));

        var snapshots = _native.GetProcessSnapshots()
            .Where(p => p.WorkingSetBytes > 20L << 20)
            .Where(p => !_whitelist.IsExcluded(p.Name))
            .Where(p => !_whitelist.IsSystemCritical(p.Name))
            .Where(p => !_guard.IsProtected(p.Pid))
            .OrderByDescending(p => p.WorkingSetBytes).Take(30).ToList();
        var reportJson = report is null ? "暂无分析报告" : JsonSerializer.Serialize(report);
        var processJson = JsonSerializer.Serialize(snapshots.Select(p => new
        {
            name = p.Name, pid = p.Pid, memoryMB = p.WorkingSetBytes >> 20, path = p.Path ?? ""
        }));
        var historyText = string.Join("\n", history.TakeLast(12).Select(m => $"{m.Role}: {m.Content}"));
        var system = "你是 Windows 内存管家助手。你只能分析和生成执行计划，绝不能声称已经执行任何操作。" +
            "所有清理或结束进程都必须等待用户在界面中确认。不要建议结束系统关键、前台或防误杀进程。" +
            "用户说清理线程时，按进程工作集或待机列表解释，不要实现单独结束线程。" +
            "请只输出 JSON：{\"answer\":\"回答\",\"plan\":{\"operation\":\"none|clean_working_sets|purge_standby|terminate_processes\",\"targets\":[\"进程名.exe\"],\"reason\":\"理由\",\"risk\":\"low|medium|high\"}}。" +
            "不需要执行时 plan 为 null，回答使用" + (_l10n.CurrentLanguage == "zh-CN" ? "中文" : "English") + "。";
        var prompt = $"当前报告：{reportJson}\n当前可分析进程：{processJson}\n对话历史：\n{historyText}\n用户最新消息：{userMessage}";
        var response = await _client.ChatAsync(profile, system, prompt, ct);
        _stats.Record(new TokenUsageRecord(DateTimeOffset.Now, profile.Name, profile.Model,
            response.Usage.InputTokens, response.Usage.OutputTokens, AnalysisTrigger.Conversation, profile.Id));
        return Parse(response.Content, response.Usage);
    }

    public static AnalysisChatResponse Parse(string content, LlmUsage usage)
    {
        try
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return new AnalysisChatResponse(content.Trim(), null, usage);
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;
            var answer = root.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() ?? "" : content.Trim();
            AnalysisActionPlan? plan = null;
            if (root.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                var operation = p.TryGetProperty("operation", out var o) && o.ValueKind == JsonValueKind.String
                    ? NormalizeOperation(o.GetString()) : "none";
                if (operation != "none")
                {
                    var targets = p.TryGetProperty("targets", out var t) && t.ValueKind == JsonValueKind.Array
                        ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => NormalizeProcessName(x.GetString() ?? ""))
                            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList()
                        : new List<string>();
                    var reason = p.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                    var risk = p.TryGetProperty("risk", out var rk) && rk.ValueKind == JsonValueKind.String ? rk.GetString() ?? "medium" : "medium";
                    plan = new AnalysisActionPlan(operation, targets, reason, risk);
                }
            }
            return new AnalysisChatResponse(answer, plan, usage);
        }
        catch { return new AnalysisChatResponse(content.Trim(), null, usage); }
    }

    private static string NormalizeOperation(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "clean_working_sets" or "working_sets" or "compress" => "clean_working_sets",
        "purge_standby" or "standby" => "purge_standby",
        "terminate_processes" or "terminate" or "kill" => "terminate_processes",
        _ => "none"
    };

    private static string NormalizeProcessName(string name)
    {
        var value = name.Trim();
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }
}



