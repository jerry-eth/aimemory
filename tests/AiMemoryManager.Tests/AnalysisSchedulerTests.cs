using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class AnalysisSchedulerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly FakeNativeMemoryApi _native = new();
    private readonly FakeAnalysis _analysis = new();
    private DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    // AnalysisService 无接口,用子类重写?——不行,签名非 virtual。
    // 方案:AnalysisScheduler 依赖委托而非具体类(见 Step 3 说明),测试注入假委托。
    private sealed class FakeAnalysis
    {
        public List<AnalysisTrigger> Calls = new();
        public Func<AnalysisTrigger, Task> OnCall = _ => Task.CompletedTask;
        public Task Run(AnalysisTrigger t, CancellationToken ct) { Calls.Add(t); return OnCall(t); }
    }

    public AnalysisSchedulerTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(Path.Combine(_dir, "s.json"));
        _settings.Load();
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private void SetUsage(double percent) =>
        _native.Memory = new SystemMemoryInfo(1000, (long)(1000 * (1 - percent / 100)));

    [Fact] public async Task 阈值触发_超限且未达每日上限时分析()
    {
        _settings.Current.LlmThresholdTriggerEnabled = true;
        _settings.Current.LlmDailyCallCap = 10;
        SetUsage(90);
        var sched = new AnalysisScheduler(_settings, _native, _analysis.Run,
            new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => _now), () => _now);
        await sched.TickAsync();
        Assert.Equal(new[] { AnalysisTrigger.Threshold }, _analysis.Calls);
    }

    [Fact] public async Task 达每日上限后不再阈值触发()
    {
        _settings.Current.LlmThresholdTriggerEnabled = true;
        _settings.Current.LlmDailyCallCap = 1;
        SetUsage(90);
        var stats = new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => _now);
        stats.Record(new TokenUsageRecord(_now, "p", "m", 1, 1, AnalysisTrigger.Threshold));
        var sched = new AnalysisScheduler(_settings, _native, _analysis.Run, stats, () => _now);
        await sched.TickAsync();
        Assert.Empty(_analysis.Calls);
    }

    [Fact] public async Task 月度Token预算用完后不再自动触发()
    {
        _settings.Current.LlmThresholdTriggerEnabled = true;
        _settings.Current.MonthlyTokenBudget = 100;
        SetUsage(90);
        var stats = new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => _now);
        stats.Record(new TokenUsageRecord(_now, "p", "m", 60, 60, AnalysisTrigger.Manual));
        var sched = new AnalysisScheduler(_settings, _native, _analysis.Run, stats, () => _now);
        await sched.TickAsync();
        Assert.Empty(_analysis.Calls);
    }

    [Fact] public async Task 定时触发_到点执行并重置计时()
    {
        _settings.Current.LlmTimerTriggerEnabled = true;
        _settings.Current.LlmTimerIntervalHours = 6;
        SetUsage(10);
        var sched = new AnalysisScheduler(_settings, _native, _analysis.Run,
            new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => _now), () => _now);
        await sched.TickAsync();                       // 首次不触发
        Assert.Empty(_analysis.Calls);
        _now += TimeSpan.FromHours(7);
        await sched.TickAsync();
        Assert.Equal(new[] { AnalysisTrigger.Timer }, _analysis.Calls);
    }

    [Fact] public async Task 无激活档案时不触发()  // 由调用方保证,此测试验证异常吞掉不炸
    {
        _settings.Current.LlmThresholdTriggerEnabled = true;
        SetUsage(95);
        _analysis.OnCall = _ => throw new InvalidOperationException("无档案");
        var sched = new AnalysisScheduler(_settings, _native, _analysis.Run,
            new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => _now), () => _now);
        await sched.TickAsync();                       // 不抛异常
        Assert.Single(_analysis.Calls);                // 尝试过但被吞
    }

    [Fact] public async Task 阈值未超限不触发()
    {
        _settings.Current.LlmThresholdTriggerEnabled = true;
        SetUsage(50);
        var sched = new AnalysisScheduler(_settings, _native, _analysis.Run,
            new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => _now), () => _now);
        await sched.TickAsync();
        Assert.Empty(_analysis.Calls);
    }
}
