using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class AnalysisCacheServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
    private AnalysisCacheService Svc() => new(Path.Combine(_dir, "cache.json"), () => _now);
    private static IReadOnlyList<AnalysisSuggestion> Sug(string name) =>
        new[] { new AnalysisSuggestion(name, "compress", "r", "low") };
    public AnalysisCacheServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void 存入后可取出()
    {
        var c = Svc();
        c.Store("h1", Sug("a"));
        Assert.True(c.TryGet("h1", out var s));
        Assert.Equal("a", s[0].ProcessName);
    }

    [Fact] public void 未命中返回false()
    {
        Assert.False(Svc().TryGet("nope", out _));
    }

    [Fact] public void 超过24小时TTL后失效()
    {
        var c = Svc();
        c.Store("h1", Sug("a"));
        _now += TimeSpan.FromHours(25);
        Assert.False(c.TryGet("h1", out _));
    }

    [Fact] public void 持久化跨实例且过期项被清理()
    {
        Svc().Store("old", Sug("a"));
        _now += TimeSpan.FromHours(30);
        var c2 = Svc();
        c2.Store("new", Sug("b"));             // Store 时清理过期项
        var c3 = Svc();
        Assert.False(c3.TryGet("old", out _));
        Assert.True(c3.TryGet("new", out _));
    }
}
