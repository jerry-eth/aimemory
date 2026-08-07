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
}
