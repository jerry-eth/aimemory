namespace AiMemoryManager.Models;

public class AppSettings
{
    public string Language { get; set; } = "auto";            // "auto" | "zh-CN" | "en"
    public bool RulesMasterEnabled { get; set; } = true;      // 自动清理规则总开关(FR-3.3)
    public double ThresholdPercent { get; set; } = 80;
    public int SustainSeconds { get; set; } = 30;
    public bool ThresholdRuleEnabled { get; set; } = false;
    public bool TimerRuleEnabled { get; set; } = false;
    public int TimerIntervalMinutes { get; set; } = 60;
    public bool AutoCleanIncludeL2 { get; set; } = false;
    public bool OnlyWhenNotFullscreen { get; set; } = true;
    public bool AnimationsEnabled { get; set; } = true;
    public List<string> ExcludedProcesses { get; set; } = new();

    // M2:LLM 集成与泄漏检测
    public string? ActiveProfileId { get; set; }
    public string CustomInstructions { get; set; } = "";
    public bool LlmThresholdTriggerEnabled { get; set; } = false;
    public int LlmDailyCallCap { get; set; } = 10;
    public bool LlmTimerTriggerEnabled { get; set; } = false;
    public int LlmTimerIntervalHours { get; set; } = 6;
    public int MonthlyTokenBudget { get; set; } = 0;   // 0 = 不限
    public bool LeakDetectionEnabled { get; set; } = true;
    public int LeakGrowthThresholdMb { get; set; } = 500;
    public int LeakWindowMinutes { get; set; } = 30;
}
