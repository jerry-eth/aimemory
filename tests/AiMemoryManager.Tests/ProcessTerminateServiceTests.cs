using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class ProcessTerminateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNativeMemoryApi _native;
    private readonly WhitelistService _wl;
    private readonly KillLogService _killLog;
    private readonly ProcessTerminateService _svc;
    private readonly List<int> _terminated = new();

    public ProcessTerminateServiceTests()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsService(Path.Combine(_dir, "s.json"));
        settings.Load();
        _wl = new WhitelistService(settings);
        _native = new FakeNativeMemoryApi
        {
            Processes =
            {
                new(1, "chrome", @"C:\chrome.exe", 900L << 20, true),
                new(2, "csrss", null, 200L << 20, false),
                new(3, "myapp", null, 300L << 20, true),
                new(4, "game", null, 500L << 20, true),
            },
            ForegroundPid = -1
        };
        _native.TerminateBehavior = pid => { _terminated.Add(pid); return (true, 0); };
        _killLog = new KillLogService(Path.Combine(_dir, "k.json"));
        _svc = new ProcessTerminateService(_native, _wl,
            new ForegroundGuard(_native, () => 999), new UnsavedStateDetector(_native), _killLog);
        _wl.Add("myapp");        // 排除清理白名单
        _wl.AddNoKill("game");   // 防误杀
    }
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public async Task 四重防护_系统关键白名单防误杀前台均被拒()
    {
        _native.ForegroundPid = 1;   // chrome 变前台
        var r = await _svc.TerminateAsync(new[] { 1, 2, 3, 4 });
        Assert.Empty(_terminated);              // 全部被拒
        Assert.Empty(r.Items);
    }

    [Fact] public async Task 正常进程被终止且记后悔药()
    {
        var r = await _svc.TerminateAsync(new[] { 1 });
        Assert.Equal(new[] { 1 }, _terminated);
        Assert.Single(r.Items);
        Assert.True(r.Items[0].Success);
        Assert.Single(_killLog.Records);
        Assert.Equal("chrome", _killLog.Records[0].Name);
        Assert.Equal(900L << 20, r.FreedBytes);  // 按快照工作集估算
    }

    [Fact] public async Task 终止失败带错误码且不记后悔药()
    {
        _native.TerminateBehavior = _ => (false, 5);
        var r = await _svc.TerminateAsync(new[] { 1 });
        Assert.False(r.Items[0].Success);
        Assert.Equal(5, r.Items[0].Win32Error);
        Assert.Empty(_killLog.Records);
    }

    [Fact] public void FilterCandidates排除四类但保留正常()
    {
        _native.ForegroundPid = 1;
        var kept = _svc.FilterCandidates(new[] { 1, 2, 3, 4 });
        Assert.Empty(kept);
        _native.ForegroundPid = -1;
        kept = _svc.FilterCandidates(new[] { 1 });
        Assert.Equal(new[] { 1 }, kept);
    }

    [Fact] public async Task TerminateCompleted事件触发()
    {
        TerminateResult? got = null;
        _svc.TerminateCompleted += (_, r) => got = r;
        await _svc.TerminateAsync(new[] { 1 });
        Assert.NotNull(got);
    }
}
