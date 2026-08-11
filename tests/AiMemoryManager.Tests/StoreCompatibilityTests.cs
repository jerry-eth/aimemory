using System.IO;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public sealed class StoreCompatibilityTests
{
    [Fact]
    public void 商店版L2执行器不可用且不会注册计划任务()
    {
        var executor = new UnavailableL2Executor();
        Assert.False(executor.IsAvailable);
        Assert.False(executor.IsHelperTaskRegistered);
        Assert.Throws<NotSupportedException>(() => executor.RegisterHelperTask());
    }

    [Fact]
    public async Task 商店版L2执行器拒绝待机列表清理()
    {
        var executor = new UnavailableL2Executor();
        await Assert.ThrowsAsync<NotSupportedException>(() => executor.PurgeStandbyListAsync(CancellationToken.None));
    }

    [Fact]
    public void Store清单不声明受限提权和自动启动扩展()
    {
        var manifest = File.ReadAllText(FindRepositoryRoot(), System.Text.Encoding.UTF8);
        Assert.DoesNotContain("allowElevation", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("startupTask", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runFullTrust", manifest, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AiMemoryManager.sln")))
                return Path.Combine(directory.FullName, "packaging", "Package.Store.appxmanifest");
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("未找到仓库根目录");
    }
}
