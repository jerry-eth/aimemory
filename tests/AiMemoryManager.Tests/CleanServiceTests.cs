using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class FakeL2 : IL2Executor
{
    public bool IsHelperTaskRegistered => true;
    public void RegisterHelperTask() { }
    public long Freed { get; set; } = 500L << 20;
    public int Calls { get; private set; }
    public Task<long> PurgeStandbyListAsync(CancellationToken ct) { Calls++; return Task.FromResult(Freed); }
}

public class CleanServiceTests
{
    // 以下为 Task 6 既有用例(保留,不随本任务测试文件重写而丢失)
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

    private static (CleanService svc, FakeNativeMemoryApi native, FakeL2 l2) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "s.json"));
        settings.Load();
        var native = new FakeNativeMemoryApi
        {
            Processes =
            {
                new(1, "chrome", null, 800L << 20, true),
                new(2, "code", null, 500L << 20, true),
                new(3, "csrss", null, 100L << 20, false),   // 100MB:超过 20MB 阈值过滤,使 IsSystemCritical 豁免断言真正承重
            },
            ForegroundPid = 1
        };
        var wl = new WhitelistService(settings);
        wl.Add("code");
        var guard = new ForegroundGuard(native, () => 999);
        var l2 = new FakeL2();
        return (new CleanService(native, wl, l2, guard), native, l2);
    }

    [Fact] public async Task L1_跳过白名单_系统关键_前台进程()
    {
        var (svc, native, _) = Create();
        var r = await svc.RunL1Async(CleanTrigger.Manual);
        Assert.DoesNotContain(2, native.EmptiedPids);  // 白名单
        Assert.DoesNotContain(3, native.EmptiedPids);  // 系统关键
        Assert.DoesNotContain(1, native.EmptiedPids);  // 前台
        Assert.Equal(0, r.ProcessCount);
    }

    [Fact] public async Task L1_普通进程被清理并统计()
    {
        var (svc, native, _) = Create();
        native.ForegroundPid = -1;
        // brief 笔误修正:前台豁免解除后 chrome(pid 1)同样满足清理条件,会导致 EmptiedPids=[1,4];
        // 本用例聚焦"普通进程被清理并统计",移除 chrome 使夹具与断言一致(豁免语义已由上一用例覆盖)
        native.Processes.RemoveAll(p => p.Pid == 1);
        native.Processes.Add(new(4, "notepad", null, 100L << 20, true));
        var r = await svc.RunL1Async(CleanTrigger.Manual);
        Assert.Single(native.EmptiedPids);
        Assert.Equal(100L << 20, r.FreedBytes);
        Assert.Equal(1, r.ProcessCount);
        Assert.Equal(CleanTrigger.Manual, r.Trigger);
    }

    [Fact]
    public async Task L1_指定进程只清理勾选目标并继续应用保护规则()
    {
        var (svc, native, _) = Create();
        native.ForegroundPid = -1;
        var r = await svc.RunL1Async(CleanTrigger.Analysis, new[] { 1, 2, 3 });
        Assert.Equal(new[] { 1 }, native.EmptiedPids);
        Assert.Equal(100L << 20, r.FreedBytes);
        Assert.Equal(1, r.ProcessCount);
    }
    [Fact] public async Task L2_调用提权执行器并回报释放量()
    {
        var (svc, _, l2) = Create();
        var r = await svc.RunL2Async(CleanTrigger.Manual);
        Assert.Equal(1, l2.Calls);
        Assert.Equal(500L << 20, r.FreedBytes);
    }

    [Fact] public async Task 清理完成触发事件()
    {
        var (svc, native, _) = Create();
        native.ForegroundPid = -1;
        CleanResult? got = null;
        svc.CleanCompleted += (_, r) => got = r;
        await svc.RunL1Async(CleanTrigger.Tray);
        Assert.NotNull(got);
        Assert.Equal(CleanTrigger.Tray, got!.Trigger);
    }
}
