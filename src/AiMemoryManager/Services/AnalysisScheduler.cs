using System.Diagnostics;
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class AnalysisScheduler
{
    private readonly SettingsService _settings;
    private readonly INativeMemoryApi _native;
    private readonly Func<AnalysisTrigger, CancellationToken, Task> _run;
    private readonly TokenStatsService _stats;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _lastTimerRun;
    private bool _thresholdFiredToday;         // 超限期间只触发一次,回落后解锁

    public AnalysisScheduler(SettingsService settings, INativeMemoryApi native,
        Func<AnalysisTrigger, CancellationToken, Task> runAnalysis,
        TokenStatsService stats, Func<DateTimeOffset> clock)
    {
        (_settings, _native, _run, _stats, _clock) = (settings, native, runAnalysis, stats, clock);
        _lastTimerRun = clock();
    }

    public async Task TickAsync()
    {
        try
        {
            var s = _settings.Current;
            var now = _clock();
            bool autoAllowed = _stats.IsAutoTriggerAllowed(s.MonthlyTokenBudget);

            if (s.LlmThresholdTriggerEnabled && autoAllowed)
            {
                bool over = _native.GetSystemMemory().UsedPercent >= s.ThresholdPercent;
                if (over && !_thresholdFiredToday && _stats.TodayAutoCallCount() < s.LlmDailyCallCap)
                {
                    _thresholdFiredToday = true;
                    await _run(AnalysisTrigger.Threshold, CancellationToken.None);
                }
                else if (!over) _thresholdFiredToday = false;   // 回落后允许下次
            }

            if (s.LlmTimerTriggerEnabled && autoAllowed
                && now - _lastTimerRun >= TimeSpan.FromHours(s.LlmTimerIntervalHours))
            {
                _lastTimerRun = now;
                await _run(AnalysisTrigger.Timer, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("AnalysisScheduler: " + ex);   // 无档案/网络失败等,自动触发静默降级
        }
    }
}
