using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class KillLogServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    public KillLogServiceTests() { Directory.CreateDirectory(_dir); _path = Path.Combine(_dir, "k.json"); }
    public void Dispose() => Directory.Delete(_dir, true);

    private static KillRecord R(int pid, string? path = @"C:\apps\x.exe") =>
        new(DateTimeOffset.Now, pid, "x", path, null);

    [Fact] public void 记录新在前且超20截断()
    {
        var s = new KillLogService(_path);
        for (int i = 1; i <= 25; i++) s.Record(R(i));
        Assert.Equal(20, s.Records.Count);
        Assert.Equal(25, s.Records[0].Pid);
    }

    [Fact] public void 命令行由提供器补全()
    {
        var s = new KillLogService(_path, commandLineProvider: pid => pid == 7 ? "\"C:\\apps\\x.exe\" /fast" : null);
        s.Record(R(7));
        Assert.Equal("\"C:\\apps\\x.exe\" /fast", s.Records[0].Arguments);
        s.Record(R(8));
        Assert.Null(s.Records[0].Arguments);
    }

    [Fact] public void Restart调用starter并返回true()
    {
        string? gotPath = null, gotArgs = null;
        var s = new KillLogService(_path, starter: (p, a) => { gotPath = p; gotArgs = a; });
        var rec = R(1) with { Arguments = "/fast" };
        // Path 指向的文件须存在:
        var realExe = Path.Combine(_dir, "x.exe");
        File.WriteAllText(realExe, "dummy");
        rec = rec with { Path = realExe };
        Assert.True(s.Restart(rec));
        Assert.Equal(realExe, gotPath);
        Assert.Equal("/fast", gotArgs);
    }

    [Fact] public void Restart文件不存在返回false不调starter()
    {
        bool called = false;
        var s = new KillLogService(_path, starter: (_, _) => called = true);
        Assert.False(s.Restart(R(1, @"C:\不存在\x.exe")));
        Assert.False(called);
    }

    [Fact] public void 持久化跨实例()
    {
        var s = new KillLogService(_path);
        s.Record(R(3));
        Assert.Single(new KillLogService(_path).Records);
    }
}
