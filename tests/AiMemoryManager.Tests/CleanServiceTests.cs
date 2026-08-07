using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class CleanServiceTests
{
    private sealed class FakeL2Executor : IL2Executor
    {
        public bool IsHelperTaskRegistered { get; set; } = true;
        public long FreedBytes { get; set; }
        public int RegisterCount { get; private set; }
        public void RegisterHelperTask() { RegisterCount++; IsHelperTaskRegistered = true; }
        public Task<long> PurgeStandbyListAsync(CancellationToken ct) => Task.FromResult(FreedBytes);
    }

    [Fact]
    public async Task IL2Executor_可用Fake实现_供CleanService测试注入()
    {
        IL2Executor fake = new FakeL2Executor { FreedBytes = 12345 };
        Assert.True(fake.IsHelperTaskRegistered);
        Assert.Equal(12345, await fake.PurgeStandbyListAsync(CancellationToken.None));
    }

    [Fact]
    public void IsHelperTaskRegistered_查询不抛异常且结果可重复()
    {
        // 无需管理员:schtasks /query 对不存在的任务返回非零退出码
        var svc = new ElevatedL2Service("dummy.exe");
        var first = svc.IsHelperTaskRegistered;
        Assert.Equal(first, svc.IsHelperTaskRegistered);
    }

    [Fact]
    public void CleanResult_记录类型字段齐全()
    {
        var r = new CleanResult(DateTimeOffset.UnixEpoch, CleanLevel.L2, 4096, 3, CleanTrigger.Manual);
        Assert.Equal(CleanLevel.L2, r.Level);
        Assert.Equal(4096, r.FreedBytes);
        Assert.Equal(3, r.ProcessCount);
        Assert.Equal(CleanTrigger.Manual, r.Trigger);
    }
}
