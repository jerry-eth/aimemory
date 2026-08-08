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

    // M3:L3 终止与系统设置
    public List<string> NoKillProcesses { get; set; } = new();   // FR-7.2 防误杀名单
    public bool AutoStartEnabled { get; set; } = false;          // FR-8.3
    public bool NotificationsEnabled { get; set; } = true;       // FR-8.4
    public int HotkeyModifiers { get; set; } = 0x0002 | 0x0004;  // FR-8.5: MOD_CONTROL|MOD_SHIFT
    public int HotkeyKey { get; set; } = 0x4D;                   // 'M'
}
