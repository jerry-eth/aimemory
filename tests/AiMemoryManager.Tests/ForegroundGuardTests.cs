using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class ForegroundGuardTests
{
    [Fact] public void 前台进程受保护()
    {
        var fake = new FakeNativeMemoryApi { ForegroundPid = 1234 };
        var g = new ForegroundGuard(fake, () => 999);
        Assert.True(g.IsProtected(1234));
        Assert.False(g.IsProtected(4321));
    }

    [Fact] public void 本进程始终受保护()
    {
        var fake = new FakeNativeMemoryApi();
        var g = new ForegroundGuard(fake, () => 999);
        Assert.True(g.IsProtected(999));
    }

    [Fact] public void 全屏时且设置开启_抑制自动清理()
    {
        var fake = new FakeNativeMemoryApi { FullscreenActive = true };
        var g = new ForegroundGuard(fake, () => 1) { IsFullscreenSettingEnabled = true };
        Assert.True(g.ShouldSuppressAutoClean());
        g.IsFullscreenSettingEnabled = false;
        Assert.False(g.ShouldSuppressAutoClean());
    }
}
