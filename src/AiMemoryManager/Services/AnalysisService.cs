using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class AnalysisService
{
    private readonly INativeMemoryApi _native;
    private readonly WhitelistService _whitelist;
    private readonly ForegroundGuard _guard;
    private readonly LlmProfileService _profiles;
    private readonly PromptTemplateService _prompts;
    private readonly ILlmClient _client;
    private readonly AnalysisCacheService _cache;
    private readonly TokenStatsService _stats;
    private readonly SettingsService _settings;
    private readonly LocalizationService _l10n;

    public event EventHandler<AnalysisResult>? AnalysisCompleted;

    public AnalysisService(INativeMemoryApi native, WhitelistService whitelist, ForegroundGuard guard,
        LlmProfileService profiles, PromptTemplateService prompts, ILlmClient client,
        AnalysisCacheService cache, TokenStatsService stats, SettingsService settings, LocalizationService l10n)
        => (_native, _whitelist, _guard, _profiles, _prompts, _client, _cache, _stats, _settings, _l10n)
         = (native, whitelist, guard, profiles, prompts, client, cache, stats, settings, l10n);

    public Task<AnalysisResult> AnalyzeAsync(AnalysisTrigger trigger, bool forceRefresh = false, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var profile = _profiles.GetActive()
                ?? throw new InvalidOperationException("尚未配置大模型档案,请先在“大模型”页添加");

            // 1. 快照 + 发送前过滤(白名单/系统关键/前台一律不发给 LLM)
            var snapshots = _native.GetProcessSnapshots()
                .Where(p => p.WorkingSetBytes > 20L << 20)
                .Where(p => !_whitelist.IsExcluded(p.Name))
                .Where(p => !_whitelist.IsSystemCritical(p.Name))
                .Where(p => !_guard.IsProtected(p.Pid))
                .ToList();
            var memory = _native.GetSystemMemory();
            var template = _prompts.GetDefault();
            string language = _l10n.CurrentLanguage == "zh-CN" ? "中文" : "English";
            string customInstructions = _settings.Current.CustomInstructions;

            // 2. 缓存(forceRefresh 时跳过读取,但仍写入)
            var hash = AnalysisPromptBuilder.SnapshotHash(snapshots, profile.Model, template.Content,
                customInstructions, language);
            if (!forceRefresh && _cache.TryGet(hash, out var cached))
                return Finish(new AnalysisResult(DateTimeOffset.Now, cached, new LlmUsage(0, 0), profile.Model, true, trigger));

            // 3. LLM 调用(系统提示词中的占位符替换为实际片段)
            var fragments = AnalysisPromptBuilder.BuildFragments(snapshots, memory, customInstructions, language);
            var systemPrompt = AnalysisPromptBuilder.RenderTemplate(template.Content, fragments);
            var userPrompt = AnalysisPromptBuilder.BuildUserPrompt(
                snapshots, memory, customInstructions, language);
            var resp = await _client.ChatAsync(profile, systemPrompt, userPrompt, ct);

            // 4. 解析 + 输出侧硬过滤(模型不可信)
            var suggestions = AnalysisResultParser.Parse(resp.Content)
                .Where(s => !_whitelist.IsExcluded(s.ProcessName))
                .Where(s => !_whitelist.IsSystemCritical(s.ProcessName))
                // FR-7.2:防误杀名单进程永不进入 L3(terminate)候选,compress 建议不受影响
                .Where(s => s.Action != "terminate" || !_whitelist.IsNoKill(s.ProcessName))
                .ToList();

            // 5. Token 记录 + 缓存
            _stats.Record(new TokenUsageRecord(DateTimeOffset.Now, profile.Name, profile.Model,
                resp.Usage.InputTokens, resp.Usage.OutputTokens, trigger));
            _cache.Store(hash, suggestions);

            return Finish(new AnalysisResult(DateTimeOffset.Now, suggestions, resp.Usage, profile.Model, false, trigger));

            AnalysisResult Finish(AnalysisResult r)
            {
                AnalysisCompleted?.Invoke(this, r);
                return r;
            }
        }, ct);
}
