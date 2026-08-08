using System.IO;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void 写入后内容正确且不留tmp文件()
    {
        var p = Path.Combine(_dir, "a.json");
        AtomicFile.WriteAllText(p, "{\"x\":1}");
        Assert.Equal("{\"x\":1}", File.ReadAllText(p));
        Assert.False(File.Exists(p + ".tmp"));
    }

    [Fact] public void 覆盖已存在文件()
    {
        var p = Path.Combine(_dir, "a.json");
        File.WriteAllText(p, "old");
        AtomicFile.WriteAllText(p, "new");
        Assert.Equal("new", File.ReadAllText(p));
    }

    [Fact] public void 目录不存在时自动创建()
    {
        var p = Path.Combine(_dir, "sub", "a.json");
        AtomicFile.WriteAllText(p, "x");
        Assert.True(File.Exists(p));
    }
}
