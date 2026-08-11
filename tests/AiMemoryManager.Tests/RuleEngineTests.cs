using System.IO; // 补充:brief 测试代码缺少此 using,导致 Path/Directory 编译失败(仅此一处 fixture 修正)
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class RuleEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly FakeNativeMemoryApi _native = new();
    private readonly ForegroundGuard _guard;
    private DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    public RuleEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(Path.Combine(_dir, "s.json"));
        _settings.Load();
        _guard = new ForegroundGuard(_native, () => 1);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private RuleEngine Create(out List<CleanRequest> fired, Func<bool>? l2Available = null)
    {
        var list = new List<CleanRequest>();
        var e = new RuleEngine(_settings, _native, _guard, () => _now, l2Available);
        e.CleanRequested += (_, r) => list.Add(r);
        fired = list;
        return e;
    }

    private void SetUsage(double percent) =>
        _native.Memory = new SystemMemoryInfo(1000, (long)(1000 * (1 - percent / 100)));

    [Fact] public void 阈值规则_未持续超阈不触发()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 30;         // 需要 3 次连续 tick
        var e = Create(out var fired);
        SetUsage(90);
        e.Tick(); e.Tick();
        Assert.Empty(fired);
    }

    [Fact] public void 阈值规则_持续超阈后触发且带冷却()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 30;
        var e = Create(out var fired);
        SetUsage(90);
        e.Tick(); e.Tick(); e.Tick();
        Assert.Single(fired);
        Assert.Equal(CleanTrigger.RuleThreshold, fired[0].Trigger);
        e.Tick(); e.Tick(); e.Tick();                  // 冷却 5 分钟内不重复
        Assert.Single(fired);
        _now += TimeSpan.FromMinutes(6);               // 过冷却后再触发
        e.Tick(); e.Tick(); e.Tick();
        Assert.Equal(2, fired.Count);
    }

    [Fact] public void 占用回落后计数清零()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 30;
        var e = Create(out var fired);
        SetUsage(90); e.Tick(); e.Tick();
        SetUsage(50); e.Tick();
        SetUsage(90); e.Tick(); e.Tick();
        Assert.Empty(fired);
    }

    [Fact] public void 全屏时阈值规则被抑制()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 10;
        _settings.Current.OnlyWhenNotFullscreen = true;
        _guard.IsFullscreenSettingEnabled = true;
        _native.FullscreenActive = true;
        var e = Create(out var fired);
        SetUsage(95);
        e.Tick(); e.Tick(); e.Tick();
        Assert.Empty(fired);
    }

    [Fact] public void 定时规则_到点触发()
    {
        _settings.Current.TimerRuleEnabled = true;
        _settings.Current.TimerIntervalMinutes = 60;
        var e = Create(out var fired);
        SetUsage(10);
        e.Tick();                                       // 首次不触发
        _now += TimeSpan.FromMinutes(61);
        e.Tick();
        Assert.Single(fired);
        Assert.Equal(CleanTrigger.RuleTimer, fired[0].Trigger);
    }

    [Fact] public void 总开关关闭_阈值与定时规则均不触发()
    {
        _settings.Current.RulesMasterEnabled = false;
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 10;          // 1 次 tick 即满足持续条件
        _settings.Current.TimerRuleEnabled = true;
        _settings.Current.TimerIntervalMinutes = 60;
        var e = Create(out var fired);
        SetUsage(95);
        e.Tick();                                       // 阈值条件已满足,但总开关关闭
        _now += TimeSpan.FromMinutes(61);               // 定时条件也满足
        e.Tick();
        Assert.Empty(fired);
    }

    [Fact] public void 商店模式_L2不可用时自动规则降级为L1()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 10;
        _settings.Current.AutoCleanIncludeL2 = true;
        var e = Create(out var fired, () => false);
        SetUsage(95);
        e.Tick();
        Assert.Equal(CleanLevel.L1, fired[0].Level);
    }

    [Fact] public void 触发级别跟随AutoCleanIncludeL2设置()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 10;
        _settings.Current.AutoCleanIncludeL2 = true;
        var e = Create(out var fired);
        SetUsage(95);
        e.Tick();
        Assert.Equal(CleanLevel.L2, fired[0].Level);
    }
}
