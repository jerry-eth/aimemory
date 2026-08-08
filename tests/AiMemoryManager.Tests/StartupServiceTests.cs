using System.IO;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class StartupServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _runKey = new();
    private readonly SettingsService _settings;
    private readonly StartupService _svc;

    public StartupServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(Path.Combine(_dir, "s.json"));
        _settings.Load();
        _svc = new StartupService(_settings,
            setRunKey: (name, value) => _runKey[name] = value,
            getRunKey: name => _runKey.GetValueOrDefault(name));
    }
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void 默认未启用()
    {
        Assert.False(_svc.IsEnabled);
    }

    [Fact] public void 启用写注册表与设置()
    {
        _svc.SetEnabled(true);
        Assert.True(_svc.IsEnabled);
        Assert.NotNull(_runKey["AiMemoryManager"]);
        Assert.True(_settings.Current.AutoStartEnabled);
    }

    [Fact] public void 禁用清注册表与设置()
    {
        _svc.SetEnabled(true);
        _svc.SetEnabled(false);
        Assert.False(_svc.IsEnabled);
        Assert.Null(_runKey["AiMemoryManager"]);
        Assert.False(_settings.Current.AutoStartEnabled);
    }
}
