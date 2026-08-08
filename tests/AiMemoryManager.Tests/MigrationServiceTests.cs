using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class MigrationServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _src, _dst, _log;
    private readonly List<string[]> _runs = new();
    private readonly FakeNativeMemoryApi _native = new();
    private MigrationService Svc() => new(_native, _log, runner: args => { _runs.Add(args); return RunFake(args); });

    public MigrationServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _src = Path.Combine(_dir, "Games");
        _dst = Path.Combine(_dir, "D");          // 模拟目标盘根
        _log = Path.Combine(_dir, "m.json");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
        File.WriteAllText(Path.Combine(_src, "a.dat"), "data");
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private int RunFake(string[] args)
    {
        // 模拟 robocopy /E src dst:复制文件;mklink:创建标记文件充当 junction
        if (args[0] == "robocopy")
        {
            var (s, d) = (args[^2], args[^1]);
            Directory.CreateDirectory(d);
            foreach (var f in Directory.GetFiles(s)) File.Copy(f, Path.Combine(d, Path.GetFileName(f)), true);
            return 1;   // robocopy 退出码 <8 为成功
        }
        if (args[0] == "mklink") { File.WriteAllText(args[^2] + ".junction", args[^1]); return 0; }
        return 0;
    }

    [Fact] public void 占用检测_路径前缀匹配()
    {
        _native.Processes.Add(new ProcessSnapshot(1, "game", Path.Combine(_src, "game.exe"), 100L << 20, true));
        _native.Processes.Add(new ProcessSnapshot(2, "other", @"C:\other\x.exe", 100L << 20, true));
        var blocking = Svc().GetBlockingProcesses(_src);
        Assert.Equal(new[] { "game" }, blocking);
    }

    [Fact] public async Task 迁移流程_复制校验删源建Junction记日志()
    {
        var entry = await Svc().MigrateAsync(_src, _dst);
        Assert.True(File.Exists(Path.Combine(_dst, "Games", "a.dat")));   // 已复制
        Assert.False(Directory.Exists(_src));                              // 源已删
        Assert.Equal(_src, entry.Source);
        Assert.False(entry.Reverted);
        Assert.Single(Svc().Log);
    }

    [Fact] public async Task 复制失败抛异常且现场保留()
    {
        var svc = new MigrationService(_native, _log, runner: _ => 8);   // robocopy 退出码>=8 失败
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MigrateAsync(_src, _dst));
        Assert.True(Directory.Exists(_src));
        Assert.True(File.Exists(Path.Combine(_src, "a.dat")));
    }

    [Fact] public async Task 回退_删Junction移回源更新日志()
    {
        var svc = Svc();
        var entry = await svc.MigrateAsync(_src, _dst);
        Assert.True(await svc.RevertAsync(entry));
        Assert.True(File.Exists(Path.Combine(_src, "a.dat")));   // 移回
        Assert.True(svc.Log[0].Reverted);
    }
}
