using System.IO;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class WhitelistServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly WhitelistService _wl;

    public WhitelistServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        _settings.Load();
        _wl = new WhitelistService(_settings);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void Add_自动小写并去掉exe后缀()
    {
        _wl.Add("Chrome.EXE");
        Assert.Contains("chrome", _wl.Excluded);
        Assert.True(_wl.IsExcluded("CHROME"));
    }

    [Fact] public void Add_重复添加只保留一份()
    {
        _wl.Add("code"); _wl.Add("CODE");
        Assert.Equal(1, _wl.Excluded.Count(x => x == "code"));
    }

    [Fact] public void Remove_后不再排除()
    {
        _wl.Add("code"); _wl.Remove("code");
        Assert.False(_wl.IsExcluded("code"));
    }

    [Fact] public void 系统关键进程_永远视为受保护()
    {
        Assert.True(_wl.IsSystemCritical("system"));
        Assert.True(_wl.IsSystemCritical("csrss"));
        Assert.True(_wl.IsSystemCritical("explorer"));
        Assert.False(_wl.IsSystemCritical("chrome"));
    }

    [Fact] public void Add_后立即持久化到设置()
    {
        _wl.Add("notepad");
        Assert.Contains("notepad", _settings.Current.ExcludedProcesses);
    }

    [Fact] public void Import_每行一个进程名()
    {
        var f = Path.Combine(_dir, "wl.txt");
        File.WriteAllLines(f, new[] { "foo.exe", "bar", "", "  " });
        _wl.Import(f);
        Assert.True(_wl.IsExcluded("foo"));
        Assert.True(_wl.IsExcluded("bar"));
        Assert.Equal(2, _wl.Excluded.Count);
    }
}
