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
    public void Load_旧配置中的空集合和越界值会被规范化()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
        {
          "Language": "fr",
          "ThresholdPercent": 999,
          "SustainSeconds": -10,
          "ExcludedProcesses": null,
          "BlacklistProcesses": null,
          "NoKillProcesses": null,
          "LeakGrowthThresholdMb": 1,
          "LeakWindowMinutes": 999
        }
        """);

        var svc = new SettingsService(path);
        svc.Load();

        Assert.Equal("auto", svc.Current.Language);
        Assert.Equal(95, svc.Current.ThresholdPercent);
        Assert.Equal(1, svc.Current.SustainSeconds);
        Assert.NotNull(svc.Current.ExcludedProcesses);
        Assert.NotNull(svc.Current.BlacklistProcesses);
        Assert.NotNull(svc.Current.NoKillProcesses);
        Assert.Equal(50, svc.Current.LeakGrowthThresholdMb);
        Assert.Equal(120, svc.Current.LeakWindowMinutes);
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
