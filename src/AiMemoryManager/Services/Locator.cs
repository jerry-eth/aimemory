using System.IO;
using AiMemoryManager.Models;
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
    public static CleanHistoryService History { get; private set; } = null!;
    public static KillLogService KillLog { get; private set; } = null!;
    public static UnsavedStateDetector Unsaved { get; private set; } = null!;
    public static ProcessTerminateService Terminator { get; private set; } = null!;
    public static StartupService Startup { get; private set; } = null!;
    public static HotkeyService Hotkey { get; private set; } = null!;
    public static DiskScanService DiskScan { get; private set; } = null!;
    public static DiskAdviceService DiskAdvice { get; private set; } = null!;
    public static RecycleBinDeleteService RecycleBin { get; private set; } = null!;
    public static MigrationService Migration { get; private set; } = null!;

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

        // M3 接线顺序:History/KillLog/Unsaved → Terminator → Startup
        History = new CleanHistoryService(CleanHistoryService.DefaultPath());
        KillLog = new KillLogService(KillLogService.DefaultPath());
        Unsaved = new UnsavedStateDetector(Native);
        Terminator = new ProcessTerminateService(Native, Whitelist, Guard, Unsaved, KillLog);
        Startup = new StartupService(Settings);
        // 启动时按设置同步一次注册表(设置开但键被外部删除 → 补写;设置关但键残留 → 清掉)
        if (Settings.Current.AutoStartEnabled != Startup.IsEnabled)
            Startup.SetEnabled(Settings.Current.AutoStartEnabled);

        Hotkey = new HotkeyService();   // FR-8.5:窗口句柄就绪后由 App 注册

        // M3 FR-12:C 盘瘦身四服务(扫描/LLM 建议/回收站删除/跨盘迁移)
        DiskScan = new DiskScanService();
        RecycleBin = new RecycleBinDeleteService();
        Migration = new MigrationService(Native, MigrationService.DefaultLogPath());
        DiskAdvice = new DiskAdviceService(Profiles, LlmClient, L10n, TokenStats, GetAvailableFixedDrives());

        // 只有 CleanService 在 Locator 记录清理历史;Terminator 的历史由调用方 VM 记(进程页 Manual/分析页 Analysis),避免双记
        Clean.CleanCompleted += (_, r) => History.Record(
            new CleanHistoryEntry(r.Time, r.Level, r.FreedBytes, r.ProcessCount, r.Trigger));
    }

    /// <summary>FR-12 可用固定盘:Fixed + Ready + 非系统盘(DiskAdviceService 迁移目标盘过滤用)。</summary>
    private static IReadOnlyList<string> GetAvailableFixedDrives()
    {
        string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\";
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady
                && !string.Equals(d.Name.TrimEnd('\\'), sysRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name)
            .ToList();
    }
}
