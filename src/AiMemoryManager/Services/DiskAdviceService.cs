using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>LLM 可选的磁盘建议服务；本地规则始终可独立工作。</summary>
public class DiskAdviceService
{
    public const string PromptTemplate = """
        你是 Windows 磁盘瘦身顾问。用户会给你一份 C 盘候选目录扫描结果(JSON,含 path/sizeMB/fileCount/category)和可用固定盘列表。
        请输出 JSON,格式如下:
        {"cleanable":[{"path":"...","reason":"...","estMB":数字}],
         "migratable":[{"path":"...","reason":"...","target":"盘符,如 D:"}]}
        规则:
        1. 只建议输入清单里出现过的路径,不要编造路径。
        2. 系统路径(如 C:\Windows、Program Files)绝不出现。
        3. cleanable 只建议临时文件和浏览器缓存，estMB 为估计可释放的 MB 数。
        4. migratable 只建议用户数据目录，target 必须是可用固定盘之一。
        5. reason 用{语言}写,简短说明理由。
        """;

    private readonly LlmProfileService _profiles;
    private readonly ILlmClient _client;
    private readonly LocalizationService _l10n;
    private readonly TokenStatsService _stats;
    private readonly IReadOnlyList<string> _availableFixedDrives;
    private readonly LocalDiskRuleService _localRules;

    public event EventHandler<DiskAdvice>? AdviceCompleted;

    public DiskAdviceService(LlmProfileService profiles, ILlmClient client, LocalizationService l10n,
        TokenStatsService stats, IReadOnlyList<string> availableFixedDrives)
    {
        _profiles = profiles;
        _client = client;
        _l10n = l10n;
        _stats = stats;
        _availableFixedDrives = availableFixedDrives;
        _localRules = new LocalDiskRuleService(availableFixedDrives);
    }

    public DiskAdvice GetLocalAdvice(IReadOnlyList<FolderSizeInfo> scan) =>
        _localRules.Generate(scan);

    /// <summary>UI 使用的入口：没有档案、超时、网络错误和非法 JSON 都自动回退本地规则。</summary>
    public async Task<DiskAdvice> AnalyzeWithFallbackAsync(IReadOnlyList<FolderSizeInfo> scan, CancellationToken ct = default)
    {
        var local = GetLocalAdvice(scan);
        if (_profiles.GetActive() is null)
        {
            var fallback = local with { Source = DiskAdviceSource.LocalFallback, StatusMessage = "未配置大模型，已使用本地规则" };
            AdviceCompleted?.Invoke(this, fallback);
            return fallback;
        }
        try
        {
            return await AnalyzeAsync(scan, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var fallback = local with
            {
                Source = DiskAdviceSource.LocalFallback,
                StatusMessage = $"大模型分析不可用，已降级到本地规则：{ex.Message}"
            };
            AdviceCompleted?.Invoke(this, fallback);
            return fallback;
        }
    }

    /// <summary>保留原 API：显式 LLM 分析，供既有调用和测试使用。</summary>
    public async Task<DiskAdvice> AnalyzeAsync(IReadOnlyList<FolderSizeInfo> scan, CancellationToken ct = default)
    {
        var profile = _profiles.GetActive()
            ?? throw new InvalidOperationException("尚未配置大模型档案,请先在“大模型”页添加");

        string language = _l10n.CurrentLanguage == "zh-CN" ? "中文" : "English";
        string systemPrompt = PromptTemplate.Replace("{语言}", language);
        var scanJson = JsonSerializer.Serialize(scan.Select(s => new
        {
            path = s.Path,
            pathHint = GetPathHint(s.Path),
            sizeMB = s.SizeBytes >> 20,
            fileCount = s.FileCount,
            category = s.Category.ToString(),
        }));
        string userPrompt = $"扫描结果:\n{scanJson}\n可用固定盘:{string.Join(", ", _availableFixedDrives)}\n请只输出 JSON";

        var resp = await _client.ChatAsync(profile, systemPrompt, userPrompt, ct);
        var advice = Filter(Parse(resp.Content), scan) with { Source = DiskAdviceSource.Llm };
        _stats.Record(new TokenUsageRecord(DateTimeOffset.Now, profile.Name, profile.Model,
            resp.Usage.InputTokens, resp.Usage.OutputTokens, AnalysisTrigger.Manual, profile.Id));
        AdviceCompleted?.Invoke(this, advice);
        return advice;
    }

    private static string GetPathHint(string path)
    {
        try
        {
            var parts = path.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 2 ? path : string.Join("\\", parts[^2..]);
        }
        catch { return "目录"; }
    }

    private DiskAdvice Filter(DiskAdvice raw, IReadOnlyList<FolderSizeInfo> scan)
    {
        var candidateSet = new HashSet<string>(scan.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        var categoryMap = scan.ToDictionary(s => s.Path, s => s.Category, StringComparer.OrdinalIgnoreCase);
        bool TargetOk(string target) => _availableFixedDrives.Any(d =>
            string.Equals(d.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        bool SafeClean(string path) => categoryMap.TryGetValue(path, out var category) &&
            category is DiskCategory.Temp or DiskCategory.BrowserCache &&
            !SystemPathGuard.IsProtected(path) && IsLikelyCachePath(path, category);
        bool SafeMigrate(string path) => categoryMap.TryGetValue(path, out var category) &&
            category == DiskCategory.UserFolder && !SystemPathGuard.IsProtected(path) &&
            !path.Contains("AppData", StringComparison.OrdinalIgnoreCase) &&
            !PathSafetyService.IsDriveRoot(path);

        var cleanable = raw.Cleanable
            .Where(c => candidateSet.Contains(c.Path) && SafeClean(c.Path))
            .Select(c => c with { Category = categoryMap[c.Path], EstBytes = Math.Max(0, c.EstBytes) })
            .DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var migratable = raw.Migratable
            .Where(m => candidateSet.Contains(m.Path) && SafeMigrate(m.Path) && TargetOk(m.TargetDrive))
            .Select(m => m with { Category = categoryMap[m.Path] })
            .DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DiskAdvice(cleanable, migratable);
    }

    private static bool IsLikelyCachePath(string path, DiskCategory category)
    {
        if (category == DiskCategory.Temp)
            return path.TrimEnd('\\').EndsWith("\\Temp", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("\\tmp\\", StringComparison.OrdinalIgnoreCase);
        return path.Contains("\\Cache", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\cache\\", StringComparison.OrdinalIgnoreCase);
    }

    private static DiskAdvice Parse(string llmContent)
    {
        var empty = new DiskAdvice(Array.Empty<DiskCleanableItem>(), Array.Empty<DiskMigratableItem>());
        try
        {
            int start = llmContent.IndexOf('{');
            int end = llmContent.LastIndexOf('}');
            if (start < 0 || end <= start) return empty;
            using var doc = JsonDocument.Parse(llmContent[start..(end + 1)]);
            var cleanable = new List<DiskCleanableItem>();
            if (doc.RootElement.TryGetProperty("cleanable", out var ca) && ca.ValueKind == JsonValueKind.Array)
            foreach (var item in ca.EnumerateArray())
            {
                try
                {
                    if (!item.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 } path) continue;
                    var reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                    long mb = item.TryGetProperty("estMB", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var value) ? value : 0;
                    cleanable.Add(new DiskCleanableItem(path, reason, Math.Clamp(mb, 0, 1L << 30) << 20));
                }
                catch { }
            }
            var migratable = new List<DiskMigratableItem>();
            if (doc.RootElement.TryGetProperty("migratable", out var ma) && ma.ValueKind == JsonValueKind.Array)
            foreach (var item in ma.EnumerateArray())
            {
                try
                {
                    if (!item.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 } path) continue;
                    if (!item.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() is not { Length: > 0 } target) continue;
                    var reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                    migratable.Add(new DiskMigratableItem(path, reason, target));
                }
                catch { }
            }
            return new DiskAdvice(cleanable, migratable);
        }
        catch { return empty; }
    }
}

