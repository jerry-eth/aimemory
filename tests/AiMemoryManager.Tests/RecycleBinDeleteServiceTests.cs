using System.IO;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class RecycleBinDeleteServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    public RecycleBinDeleteServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact] public void 删除目录后原路径不存在()
    {
        var target = Path.Combine(_dir, "todel");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "f.txt"), "x");
        Assert.True(new RecycleBinDeleteService().DeleteDirectoryToRecycleBin(target));
        Assert.False(Directory.Exists(target));
    }

    [Fact] public void 不存在路径返回false不抛()
    {
        Assert.False(new RecycleBinDeleteService().DeleteDirectoryToRecycleBin(Path.Combine(_dir, "nope")));
    }
}
