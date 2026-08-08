using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class CleanHistoryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    public CleanHistoryServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "h.json");
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private static CleanHistoryEntry E(int mb, CleanTrigger t = CleanTrigger.Manual) =>
        new(DateTimeOffset.Now, CleanLevel.L1, (long)mb << 20, 3, t);

    [Fact] public void 记录后新在前且触发Changed事件()
    {
        var s = new CleanHistoryService(_path);
        int fired = 0;
        s.Changed += (_, _) => fired++;
        s.Record(E(10));
        s.Record(E(20));
        Assert.Equal(20, (int)(s.Entries[0].FreedBytes >> 20));
        Assert.Equal(2, fired);
    }

    [Fact] public void 超100条截断最旧()
    {
        var s = new CleanHistoryService(_path);
        for (int i = 0; i < 110; i++) s.Record(E(i));
        Assert.Equal(100, s.Entries.Count);
        Assert.Equal(109, (int)(s.Entries[0].FreedBytes >> 20));
    }

    [Fact] public void 持久化跨实例()
    {
        var s = new CleanHistoryService(_path);
        s.Record(E(42, CleanTrigger.RuleThreshold));
        var s2 = new CleanHistoryService(_path);
        Assert.Single(s2.Entries);
        Assert.Equal(CleanTrigger.RuleThreshold, s2.Entries[0].Trigger);
    }

    [Fact] public void 损坏文件回退空列表()
    {
        File.WriteAllText(_path, "not json");
        var s = new CleanHistoryService(_path);
        Assert.Empty(s.Entries);
    }
}
