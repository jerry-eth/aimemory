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

    [Fact] public async Task 文件枚举中途抛异常时跳过而非失败()
    {
        // 惰性枚举的异常发生在 MoveNext(如 $Recycle.Bin 其他用户 SID 子目录)。
        // 修复前 try 只包住调用点,此场景会使整个 MeasureAsync 失败。
        var svc = new ThrowingScanService(_dir, throwOnFiles: true);
        var r = await svc.MeasureAsync(_dir, DiskCategory.RecycleBin);
        Assert.Equal(0, r.SizeBytes);
        Assert.Equal(0, r.FileCount);
    }

    [Fact] public async Task 子目录枚举抛异常时仍统计可访问部分()
    {
        var sub = Path.Combine(_dir, "sub");
        var svc = new ThrowingScanService(sub, throwOnFiles: true);
        var r = await svc.MeasureAsync(_dir, DiskCategory.Other);
        Assert.Equal(100, r.SizeBytes);   // 只有根目录 a.bin;sub 枚举被拒,跳过
        Assert.Equal(1, r.FileCount);
    }

    [Fact] public async Task ScanAsync中枚举失败的候选不影响其它候选()
    {
        var sub = Path.Combine(_dir, "sub");
        var svc = new ThrowingScanService(sub, throwOnFiles: true);
        var list = await svc.ScanAsync(new[]
        {
            new DiskCandidate(sub, DiskCategory.RecycleBin),
            new DiskCandidate(_dir, DiskCategory.UserFolder),
        });
        Assert.Equal(2, list.Count);
        Assert.Equal(0, list[0].SizeBytes);     // 失败候选返回 0 而不是拖垮整批
        Assert.Equal(100, list[1].SizeBytes);   // _dir 中 sub 被拒,只统计 a.bin
    }

    /// <summary>模拟枚举期(MoveNext)抛 UnauthorizedAccessException 的服务。</summary>
    private sealed class ThrowingScanService : DiskScanService
    {
        private readonly string _throwOn;
        private readonly bool _throwOnFiles;
        public ThrowingScanService(string throwOn, bool throwOnFiles)
        {
            _throwOn = throwOn;
            _throwOnFiles = throwOnFiles;
        }
        protected override IEnumerable<string> EnumerateFiles(string dir)
            => _throwOnFiles && dir == _throwOn ? Throw() : base.EnumerateFiles(dir);
        protected override IEnumerable<string> EnumerateDirectories(string dir)
            => dir == _throwOn ? Throw() : base.EnumerateDirectories(dir);
        private static IEnumerable<string> Throw()
        {
            // 非常量条件避免 CS0162;异常在首次 MoveNext 时抛出,模拟惰性枚举失败
            if (Environment.TickCount64 >= 0)
                throw new UnauthorizedAccessException("模拟枚举中途无权限");
            yield break;
        }
    }
}
