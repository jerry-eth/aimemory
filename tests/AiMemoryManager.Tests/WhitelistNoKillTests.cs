using System.IO;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class WhitelistNoKillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly WhitelistService _wl;
    public WhitelistNoKillTests()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsService(Path.Combine(_dir, "s.json"));
        settings.Load();
        _wl = new WhitelistService(settings);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void 默认名单为空()
    {
        Assert.False(_wl.IsNoKill("chrome"));
        Assert.Empty(_wl.NoKill);
    }

    [Fact] public void 添加后命中且大小写不敏感()
    {
        _wl.AddNoKill("Game.exe");
        Assert.True(_wl.IsNoKill("game.exe"));
        Assert.True(_wl.IsNoKill("GAME.EXE"));
        Assert.Single(_wl.NoKill);
    }

    [Fact] public void 移除后不再命中()
    {
        _wl.AddNoKill("game");
        _wl.RemoveNoKill("GAME");
        Assert.False(_wl.IsNoKill("game"));
    }

    [Fact] public void 持久化到settings跨实例()
    {
        _wl.AddNoKill("game");
        var settings2 = new SettingsService(Path.Combine(_dir, "s.json"));
        settings2.Load();
        var wl2 = new WhitelistService(settings2);
        Assert.True(wl2.IsNoKill("game"));
    }

    [Fact] public void 防误杀与排除清理互不影响()
    {
        _wl.AddNoKill("game");
        Assert.False(_wl.IsExcluded("game"));   // 防误杀≠排除压缩
        _wl.Add("tool");
        Assert.False(_wl.IsNoKill("tool"));     // 排除清理≠防误杀
    }
}
