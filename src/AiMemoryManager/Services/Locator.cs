using System.IO;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

/// <summary>
/// 静态服务定位器。M1 不引入完整 DI 容器,由 App 启动时调用 Init() 一次性装配。
/// </summary>
public static class Locator
{
    public static INativeMemoryApi Native { get; private set; } = new NativeMemoryApi();
    public static SettingsService Settings { get; private set; } = null!;
    public static WhitelistService Whitelist { get; private set; } = null!;
    public static ForegroundGuard Guard { get; private set; } = null!;
    public static IL2Executor L2 { get; private set; } = null!;
    public static CleanService Clean { get; private set; } = null!;
    public static RuleEngine Rules { get; private set; } = null!;
    public static MemoryMonitorService Monitor { get; private set; } = null!;
    public static LocalizationService L10n { get; private set; } = null!;
    public static LlmProfileService Profiles { get; private set; } = null!;
    public static PromptTemplateService Prompts { get; private set; } = null!;
    public static TokenStatsService TokenStats { get; private set; } = null!;
    public static ILlmClient LlmClient { get; private set; } = null!;
    public static AnalysisCacheService AnalysisCache { get; private set; } = null!;
    public static AnalysisService Analysis { get; private set; } = null!;
    public static AnalysisScheduler Scheduler { get; private set; } = null!;
    public static LeakDetectionService LeakDetection { get; private set; } = null!;

    public static void Init()
    {
        Settings = new SettingsService(SettingsService.DefaultPath());
        Settings.Load();
        Whitelist = new WhitelistService(Settings);
        Guard = new ForegroundGuard(Native, () => Environment.ProcessId);
        var helperPath = Path.Combine(AppContext.BaseDirectory, "AiMemoryManager.ElevatedHelper.exe");
        L2 = new ElevatedL2Service(helperPath);
        Clean = new CleanService(Native, Whitelist, L2, Guard);
        Rules = new RuleEngine(Settings, Native, Guard, () => DateTimeOffset.Now);
        Monitor = new MemoryMonitorService(Native);
        L10n = new LocalizationService(Path.Combine(AppContext.BaseDirectory, "Assets", "i18n"));
        if (Settings.Current.Language == "auto") L10n.SetAuto();
        else L10n.CurrentLanguage = Settings.Current.Language;

        Profiles = new LlmProfileService(LlmProfileService.DefaultPath(), Settings);
        Profiles.Load();
        Prompts = new PromptTemplateService(PromptTemplateService.DefaultPath());
        Prompts.Load();
        TokenStats = new TokenStatsService(TokenStatsService.DefaultPath(), () => DateTimeOffset.Now);
        LlmClient = new OpenAiCompatibleClient();
        AnalysisCache = new AnalysisCacheService(AnalysisCacheService.DefaultPath(), () => DateTimeOffset.Now);
        Analysis = new AnalysisService(Native, Whitelist, Guard, Profiles, Prompts, LlmClient,
            AnalysisCache, TokenStats, Settings, L10n);
        // 自动触发走默认参数(forceRefresh=false),显式 lambda 匹配委托签名
        Scheduler = new AnalysisScheduler(Settings, Native,
            (t, ct) => Analysis.AnalyzeAsync(t, false, ct), TokenStats, () => DateTimeOffset.Now);
        LeakDetection = new LeakDetectionService(Settings, () => DateTimeOffset.Now);
    }
}
