using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class DiskScanServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    public DiskScanServiceTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.bin"), new string('x', 100));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "b.bin"), new string('x', 200));
    }
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public async Task 测量含子目录的文件总大小与计数()
    {
        var r = await new DiskScanService().MeasureAsync(_dir, DiskCategory.Other);
        Assert.Equal(300, r.SizeBytes);
        Assert.Equal(2, r.FileCount);
        Assert.Equal(DiskCategory.Other, r.Category);
    }

    [Fact] public async Task 不存在路径返回零()
    {
        var r = await new DiskScanService().MeasureAsync(Path.Combine(_dir, "nope"), DiskCategory.Temp);
        Assert.Equal(0, r.SizeBytes);
    }

    [Fact] public async Task ScanAsync批量且跳过失败项()
    {
        var list = await new DiskScanService().ScanAsync(new[]
        {
            new DiskCandidate(_dir, DiskCategory.UserFolder),
            new DiskCandidate(Path.Combine(_dir, "nope"), DiskCategory.Temp),
        });
        Assert.Equal(2, list.Count);
        Assert.Equal(300, list[0].SizeBytes);
    }
}
