using System.Text.Json;
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

/// <summary>
/// 使用当前进程快照请求大模型给出白名单建议。
/// 该服务只返回候选建议，不会自动修改白名单。
/// </summary>
public sealed class WhitelistAdviceService
{
    private readonly INativeMemoryApi _native;
    private readonly WhitelistService _whitelist;
    private readonly LlmProfileService _profiles;
    private readonly ILlmClient _client;
    private readonly TokenStatsService _stats;

    public WhitelistAdviceService(INativeMemoryApi native, WhitelistService whitelist,
        LlmProfileService profiles, ILlmClient client, TokenStatsService stats)
        => (_native, _whitelist, _profiles, _client, _stats) =
            (native, whitelist, profiles, client, stats);

    public async Task<WhitelistAdviceResult> AnalyzeAsync(CancellationToken ct = default)
    {
        var profile = _profiles.GetActive()
            ?? throw new InvalidOperationException("尚未配置大模型档案,请先在“大模型”页添加");

        // System.Diagnostics.Process 的路径查询可能阻塞，放在线程池执行，避免点击按钮卡住界面。
        var candidates = await Task.Run(() => _native.GetProcessSnapshots()
            .Where(p => p.WorkingSetBytes > 10L << 20)
            .Where(p => !_whitelist.IsExcluded(p.Name))
            .Where(p => !_whitelist.IsSystemCritical(p.Name))
            .OrderByDescending(p => p.WorkingSetBytes)
            .Take(40)
            .ToList(), ct);

        if (candidates.Count == 0)
            return new WhitelistAdviceResult(Array.Empty<WhitelistAdvice>(), new LlmUsage(0, 0), profile.Model);

        var input = candidates.Select(p => new
        {
            processName = p.Name,
            path = p.Path ?? "",
            memoryMb = Math.Round(p.WorkingSetBytes / 1048576d, 1),
            hasVisibleWindow = p.HasVisibleWindow
        });
        var candidateJson = JsonSerializer.Serialize(input);
        var systemPrompt = "你是 Windows 进程白名单安全顾问。你只能根据用户提供的当前进程快照给出建议，不能执行任何操作。" +
            "普通白名单的作用是让内存清理跳过进程，并不等于防误杀名单。" +
            "只推荐稳定运行、用户通常明确需要长期保留、且加入白名单有实际意义的普通应用进程。" +
            "不要因为内存占用高就推荐。不要推荐系统关键进程、驱动、服务宿主、未知路径进程或明显的临时进程。" +
            "请严格返回 JSON 对象，格式为 {\"recommendations\":[{\"processName\":\"进程名\",\"recommended\":true,\"reason\":\"不超过80字的中文理由\"}]}。" +
            "recommendations 只能包含输入列表中的进程名；不确定时 recommended 必须为 false。";
        var userPrompt = "请分析以下未加入白名单的进程，告诉用户哪些适合加入普通白名单。" +
            "所有建议都必须由用户勾选确认后才会生效。\n当前进程：\n" + candidateJson;

        var response = await _client.ChatAsync(profile, systemPrompt, userPrompt, ct);
        var suggestions = Parse(response.Content, candidates);

        _stats.Record(new TokenUsageRecord(DateTimeOffset.Now, profile.Name, profile.Model,
            response.Usage.InputTokens, response.Usage.OutputTokens, AnalysisTrigger.Manual, profile.Id));

        return new WhitelistAdviceResult(suggestions, response.Usage, profile.Model);
    }

    private IReadOnlyList<WhitelistAdvice> Parse(string content, IReadOnlyList<ProcessSnapshot> candidates)
    {
        var allowed = candidates
            .GroupBy(p => Normalize(p.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<WhitelistAdvice>();
        try
        {
            using var doc = JsonDocument.Parse(StripMarkdown(content));
            var entries = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : FindArray(doc.RootElement);

            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var name = ReadString(entry, "processName") ?? ReadString(entry, "name") ?? ReadString(entry, "process");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!allowed.TryGetValue(Normalize(name), out var snapshot)) continue;

                var recommended = ReadBool(entry, "recommended") ??
                    IsPositive(ReadString(entry, "recommendation") ?? ReadString(entry, "action"));
                var reason = ReadString(entry, "reason") ?? ReadString(entry, "explanation") ?? "模型未提供理由";
                result.Add(new WhitelistAdvice(snapshot.Name, snapshot.Path, snapshot.WorkingSetBytes,
                    recommended, Limit(reason.Trim(), 240)));
            }
        }
        catch (JsonException)
        {
            // 模型输出不规范时返回空建议，由界面提示用户重试，不让异常退出软件。
        }

        return result
            .GroupBy(x => Normalize(x.ProcessName), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => x.Recommended)
            .ThenByDescending(x => x.WorkingSetBytes)
            .ToList();
    }

    private static IEnumerable<JsonElement> FindArray(JsonElement root)
    {
        foreach (var key in new[] { "recommendations", "suggestions", "items", "processes" })
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static string StripMarkdown(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
                text = text[(firstNewLine + 1)..lastFence].Trim();
        }
        return text;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool? ReadBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static bool IsPositive(string? value) => value?.Trim().ToLowerInvariant() is
        "true" or "yes" or "recommend" or "recommended" or "建议" or "推荐" or "适合";

    private static string Normalize(string name)
    {
        var value = name.Trim().ToLowerInvariant();
        return value.EndsWith(".exe", StringComparison.Ordinal) ? value[..^4] : value;
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}


