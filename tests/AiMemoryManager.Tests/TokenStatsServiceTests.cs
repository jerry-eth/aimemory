using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class TokenStatsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
    private TokenStatsService Svc() => new(Path.Combine(_dir, "usage.jsonl"), () => _now);
    private TokenUsageRecord Rec(int input, int output, AnalysisTrigger t, DateTimeOffset? time = null, string profile = "ds") =>
        new(time ?? _now, profile, "m", input, output, t);
    public TokenStatsServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void Record追加写入且跨实例可读()
    {
        var s = Svc();
        s.Record(Rec(100, 50, AnalysisTrigger.Manual));
        s.Record(Rec(200, 60, AnalysisTrigger.Threshold));
        var all = Svc().LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(200, all[1].InputTokens);
    }

    [Fact] public void AggregateSince只含窗口内记录()
    {
        var s = Svc();
        s.Record(Rec(100, 0, AnalysisTrigger.Manual, _now.AddDays(-2)));
        s.Record(Rec(200, 50, AnalysisTrigger.Manual));
        var agg = Svc().AggregateSince(_now.AddDays(-1));
        Assert.Equal(200, agg.InputTokens);
        Assert.Equal(50, agg.OutputTokens);
        Assert.Equal(1, agg.CallCount);
    }

    [Fact] public void TodayAutoCallCount只统计非手动触发()
    {
        var s = Svc();
        s.Record(Rec(1, 1, AnalysisTrigger.Manual));
        s.Record(Rec(1, 1, AnalysisTrigger.Threshold));
        s.Record(Rec(1, 1, AnalysisTrigger.Timer));
        s.Record(Rec(1, 1, AnalysisTrigger.Threshold, _now.AddDays(-1)));
        Assert.Equal(2, Svc().TodayAutoCallCount());
    }

    [Fact] public void 月度预算_超限后禁止自动触发()
    {
        var s = Svc();
        s.Record(Rec(600, 400, AnalysisTrigger.Manual));
        Assert.True(Svc().IsAutoTriggerAllowed(0));        // 0=不限
        Assert.True(Svc().IsAutoTriggerAllowed(2000));
        Assert.False(Svc().IsAutoTriggerAllowed(1000));    // 1000 累计已达上限
    }

    [Fact] public void 空文件聚合为0()
    {
        var agg = Svc().AggregateMonth();
        Assert.Equal(0, agg.InputTokens);
        Assert.Equal(0, agg.CallCount);
    }

    [Fact] public void 坏行被跳过()
    {
        File.WriteAllText(Path.Combine(_dir, "usage.jsonl"), "not json\n" +
            System.Text.Json.JsonSerializer.Serialize(Rec(10, 5, AnalysisTrigger.Manual)) + "\n");
        var all = Svc().LoadAll();
        Assert.Single(all);
    }
}
