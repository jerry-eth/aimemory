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

    public Task<AnalysisResult> AnalyzeAsync(AnalysisTrigger trigger, CancellationToken ct = default)
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

            // 2. 缓存
            var hash = AnalysisPromptBuilder.SnapshotHash(snapshots, profile.Model, template.Content);
            if (_cache.TryGet(hash, out var cached))
                return Finish(new AnalysisResult(DateTimeOffset.Now, cached, new LlmUsage(0, 0), profile.Model, true, trigger));

            // 3. LLM 调用
            string language = _l10n.CurrentLanguage == "zh-CN" ? "中文" : "English";
            var userPrompt = AnalysisPromptBuilder.BuildUserPrompt(
                snapshots, memory, _settings.Current.CustomInstructions, language);
            var resp = await _client.ChatAsync(profile, template.Content, userPrompt, ct);

            // 4. 解析 + 输出侧硬过滤(模型不可信)
            var suggestions = AnalysisResultParser.Parse(resp.Content)
                .Where(s => !_whitelist.IsExcluded(s.ProcessName))
                .Where(s => !_whitelist.IsSystemCritical(s.ProcessName))
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
