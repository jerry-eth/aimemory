using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    public SettingsServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Load_无配置文件时使用默认值并落盘()
    {
        var path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path);
        svc.Load();
        Assert.Equal(80, svc.Current.ThresholdPercent);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_后再Load_配置保持一致()
    {
        var path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path);
        svc.Load();
        svc.Current.ThresholdPercent = 66;
        svc.Current.ExcludedProcesses.Add("chrome");
        svc.Save();

        var svc2 = new SettingsService(path);
        svc2.Load();
        Assert.Equal(66, svc2.Current.ThresholdPercent);
        Assert.Contains("chrome", svc2.Current.ExcludedProcesses);
    }

    [Fact]
    public void Load_配置文件损坏时回退默认值()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json !!");
        var svc = new SettingsService(path);
        svc.Load();
        Assert.Equal(80, svc.Current.ThresholdPercent);
    }

    [Fact]
    public void Save_触发SettingsSaved事件()
    {
        var svc = new SettingsService(Path.Combine(_dir, "settings.json"));
        svc.Load();
        bool fired = false;
        svc.SettingsSaved += (_, _) => fired = true;
        svc.Save();
        Assert.True(fired);
    }
}
