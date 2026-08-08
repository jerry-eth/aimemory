using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>
/// 磁盘瘦身建议服务(FR-12.2):把磁盘扫描结果发给用户配置的大模型,
/// 解析结构化建议(可安全清理项 + 适合迁移项),并做三重硬过滤:
/// ①建议路径必须在实际扫描的候选集中(OrdinalIgnoreCase)
/// ②SystemPathGuard.IsProtected 必须为 false(系统路径永不出现)
/// ③迁移项的目标盘必须在可用固定盘列表中
/// 未过任何一关的建议直接丢弃,绝不展示给用户。
/// </summary>
public class DiskAdviceService
{
    /// <summary>磁盘专用提示词,内嵌常量(单一用途,不进 PromptTemplateService — YAGNI)。</summary>
    public const string PromptTemplate = """
        你是 Windows 磁盘瘦身顾问。用户会给你一份 C 盘候选目录扫描结果(JSON,含 path/sizeMB/fileCount/category)和可用固定盘列表。
        请输出 JSON,格式如下:
        {"cleanable":[{"path":"...","reason":"...","estMB":数字}],
         "migratable":[{"path":"...","reason":"...","target":"盘符,如 D:"}]}
        规则:
        1. 只建议输入清单里出现过的路径,不要编造路径。
        2. 系统路径(如 C:\Windows、Program Files)绝不出现。
        3. cleanable 是可以安全删除释放空间的内容(临时文件、缓存等),estMB 为估计可释放的 MB 数。
        4. migratable 是体积大但适合迁移到其他盘的用户数据(视频、游戏库等),target 必须是可用固定盘之一。
        5. reason 用{语言}写,简短说明理由。
        """;

    private readonly LlmProfileService _profiles;
    private readonly ILlmClient _client;
    private readonly LocalizationService _l10n;
    private readonly TokenStatsService _stats;
    private readonly IReadOnlyList<string> _availableFixedDrives;

    public event EventHandler<DiskAdvice>? AdviceCompleted;

    public DiskAdviceService(LlmProfileService profiles, ILlmClient client, LocalizationService l10n,
        TokenStatsService stats, IReadOnlyList<string> availableFixedDrives)
        => (_profiles, _client, _l10n, _stats, _availableFixedDrives)
         = (profiles, client, l10n, stats, availableFixedDrives);

    public async Task<DiskAdvice> AnalyzeAsync(IReadOnlyList<FolderSizeInfo> scan, CancellationToken ct = default)
    {
        var profile = _profiles.GetActive()
            ?? throw new InvalidOperationException("尚未配置大模型档案,请先在“大模型”页添加");

        string language = _l10n.CurrentLanguage == "zh-CN" ? "中文" : "English";
        string systemPrompt = PromptTemplate.Replace("{语言}", language);

        var scanJson = JsonSerializer.Serialize(scan.Select(s => new
        {
            path = s.Path,
            sizeMB = s.SizeBytes >> 20,
            fileCount = s.FileCount,
            category = s.Category.ToString(),
        }));
        string userPrompt = $"扫描结果:\n{scanJson}\n可用固定盘:{string.Join(", ", _availableFixedDrives)}\n请只输出 JSON";

        var resp = await _client.ChatAsync(profile, systemPrompt, userPrompt, ct);

        var advice = Filter(Parse(resp.Content), scan);

        _stats.Record(new TokenUsageRecord(DateTimeOffset.Now, profile.Name, profile.Model,
            resp.Usage.InputTokens, resp.Usage.OutputTokens, AnalysisTrigger.Manual, profile.Id));

        AdviceCompleted?.Invoke(this, advice);
        return advice;
    }

    /// <summary>三重过滤:候选集 + 系统路径守卫 + 迁移目标盘存在。</summary>
    private DiskAdvice Filter(DiskAdvice raw, IReadOnlyList<FolderSizeInfo> scan)
    {
        var candidateSet = new HashSet<string>(scan.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        bool InCandidates(string path) => candidateSet.Contains(path);
        bool TargetOk(string target) => _availableFixedDrives.Any(d =>
            string.Equals(d.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        var cleanable = raw.Cleanable
            .Where(c => InCandidates(c.Path) && !SystemPathGuard.IsProtected(c.Path))
            .ToList();
        var migratable = raw.Migratable
            .Where(m => InCandidates(m.Path) && !SystemPathGuard.IsProtected(m.Path) && TargetOk(m.TargetDrive))
            .ToList();
        return new DiskAdvice(cleanable, migratable);
    }

    /// <summary>
    /// 容错解析(与 AnalysisResultParser 同款手法):截取首个 { 到末个 },
    /// 逐条 try/catch,一条坏数据不拖垮整份建议;整体坏输出返回空建议。
    /// </summary>
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
            {
                foreach (var item in ca.EnumerateArray())
                {
                    try
                    {
                        if (!item.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
                            || p.GetString() is not { Length: > 0 } path) continue;
                        string reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                            ? r.GetString() ?? "" : "";
                        long estMB = item.TryGetProperty("estMB", out var e) && e.ValueKind == JsonValueKind.Number
                            && e.TryGetInt64(out long v) ? v : 0;
                        cleanable.Add(new DiskCleanableItem(path, reason, Math.Clamp(estMB, 0, 1L << 30) << 20));
                    }
                    catch { continue; }
                }
            }

            var migratable = new List<DiskMigratableItem>();
            if (doc.RootElement.TryGetProperty("migratable", out var ma) && ma.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ma.EnumerateArray())
                {
                    try
                    {
                        if (!item.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
                            || p.GetString() is not { Length: > 0 } path) continue;
                        if (!item.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.String
                            || t.GetString() is not { Length: > 0 } target) continue;
                        string reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                            ? r.GetString() ?? "" : "";
                        migratable.Add(new DiskMigratableItem(path, reason, target));
                    }
                    catch { continue; }
                }
            }

            return new DiskAdvice(cleanable, migratable);
        }
        catch { return empty; }
    }
}
