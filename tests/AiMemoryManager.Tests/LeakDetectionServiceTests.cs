using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class LeakDetectionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly LeakDetectionService _svc;
    private readonly List<LeakAlert> _alerts = new();

    public LeakDetectionServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(Path.Combine(_dir, "s.json"));
        _settings.Load();   // 默认 LeakDetectionEnabled=true, 500MB, 30min
        _svc = new LeakDetectionService(_settings, () => _now);
        _svc.LeakDetected += (_, a) => _alerts.Add(a);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private void Feed(int pid, string name, long mb, int minutesLater)
    {
        _now += TimeSpan.FromMinutes(minutesLater);
        _svc.Sample(new[] { new ProcessSnapshot(pid, name, null, mb << 20, true) });
    }

    [Fact] public void 持续增长超阈值触发一次告警()
    {
        Feed(1, "leaky", 100, 0);
        Feed(1, "leaky", 300, 10);
        Feed(1, "leaky", 400, 10);
        Feed(1, "leaky", 700, 10);   // 30 分钟 +600MB → 告警
        Assert.Single(_alerts);
        Assert.Equal("leaky", _alerts[0].ProcessName);
        Assert.Equal(600L << 20, _alerts[0].GrowthBytes);
    }

    [Fact] public void 窗口不足不告警()
    {
        Feed(1, "leaky", 100, 0);
        Feed(1, "leaky", 800, 10);   // 仅 10 分钟
        Assert.Empty(_alerts);
    }

    [Fact] public void 内存回落重置观察窗()
    {
        Feed(1, "app", 500, 0);
        Feed(1, "app", 900, 20);
        Feed(1, "app", 200, 5);      // 回落(可能被系统回收)→ 重置
        Feed(1, "app", 400, 10);     // 从 200 起算,窗口 15 分钟不足
        Assert.Empty(_alerts);
    }

    [Fact] public void 同一进程2小时内不重复告警()
    {
        Feed(1, "leaky", 100, 0); Feed(1, "leaky", 700, 30);
        Assert.Single(_alerts);
        Feed(1, "leaky", 800, 30); Feed(1, "leaky", 1400, 30);  // 又涨 600MB 但在冷却内
        Assert.Single(_alerts);
        _now += TimeSpan.FromHours(2);
        Feed(1, "leaky", 800, 0);    // 新基准
        Feed(1, "leaky", 1400, 30);
        Assert.Equal(2, _alerts.Count);
    }

    [Fact] public void 开关关闭时不告警()
    {
        _settings.Current.LeakDetectionEnabled = false;
        Feed(1, "leaky", 100, 0); Feed(1, "leaky", 800, 35);
        Assert.Empty(_alerts);
    }

    [Fact] public void RecentAlerts新在前且限量50()
    {
        for (int i = 1; i <= 60; i++)
        {
            var pid = 1000 + i;
            Feed(pid, $"p{i}", 100, 0);
            Feed(pid, $"p{i}", 700, 30);
        }
        Assert.Equal(50, _svc.RecentAlerts.Count);
        Assert.Equal("p60", _svc.RecentAlerts[0].ProcessName);
    }
}
