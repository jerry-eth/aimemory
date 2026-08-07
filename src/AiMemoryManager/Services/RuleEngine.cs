using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public record CleanRequest(CleanLevel Level, CleanTrigger Trigger);

public class RuleEngine
{
    public const int TickIntervalSeconds = 10;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private readonly SettingsService _settings;
    private readonly INativeMemoryApi _native;
    private readonly ForegroundGuard _guard;
    private readonly Func<DateTimeOffset> _clock;

    private int _overCount;
    private DateTimeOffset _lastFire = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTimerFire;

    public event EventHandler<CleanRequest>? CleanRequested;

    public RuleEngine(SettingsService settings, INativeMemoryApi native, ForegroundGuard guard, Func<DateTimeOffset> clock)
    {
        (_settings, _native, _guard, _clock) = (settings, native, guard, clock);
        _lastTimerFire = clock();
    }

    public void Tick()
    {
        var s = _settings.Current;
        if (!s.RulesMasterEnabled) { _overCount = 0; return; }   // 总开关关闭:全部规则静默
        _guard.IsFullscreenSettingEnabled = s.OnlyWhenNotFullscreen;
        var now = _clock();
        var level = s.AutoCleanIncludeL2 ? CleanLevel.L2 : CleanLevel.L1;

        if (s.ThresholdRuleEnabled && !_guard.ShouldSuppressAutoClean())
        {
            bool over = _native.GetSystemMemory().UsedPercent >= s.ThresholdPercent;
            _overCount = over ? _overCount + 1 : 0;
            int need = Math.Max(1, s.SustainSeconds / TickIntervalSeconds);
            if (_overCount >= need && now - _lastFire >= Cooldown)
            {
                _lastFire = now;
                _overCount = 0;
                CleanRequested?.Invoke(this, new CleanRequest(level, CleanTrigger.RuleThreshold));
            }
        }
        else _overCount = 0;

        if (s.TimerRuleEnabled && !_guard.ShouldSuppressAutoClean()
            && now - _lastTimerFire >= TimeSpan.FromMinutes(s.TimerIntervalMinutes))
        {
            _lastTimerFire = now;
            CleanRequested?.Invoke(this, new CleanRequest(level, CleanTrigger.RuleTimer));
        }
    }
}
