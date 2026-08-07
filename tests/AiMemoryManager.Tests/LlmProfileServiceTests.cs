using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class LlmProfileServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsPath;
    private readonly SettingsService _settings;
    private readonly LlmProfileService _svc;

    public LlmProfileServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "settings.json");
        _settings = new SettingsService(_settingsPath);
        _settings.Load();
        _svc = new LlmProfileService(Path.Combine(_dir, "profiles.json"), _settings);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private static LlmProfile Profile(string id, string name = "ds") => new()
    {
        Id = id, Name = name, BaseUrl = "https://api.deepseek.com/v1",
        EncryptedApiKey = SecretProtector.Protect("sk-x"), Model = "deepseek-v4-flash"
    };

    [Fact] public void Save新增后Load可还原且密钥仍为密文()
    {
        var profile = Profile("a1");
        _svc.Save(profile);
        var raw = File.ReadAllText(Path.Combine(_dir, "profiles.json"));
        Assert.DoesNotContain("sk-x", raw);   // 落盘不含明文

        var svc2 = new LlmProfileService(Path.Combine(_dir, "profiles.json"), _settings);
        svc2.Load();
        var p = Assert.Single(svc2.Profiles);
        // DPAPI 密文含随机盐,两次 Protect 结果不同;改为校验落盘后密文原样还原
        Assert.Equal(profile.EncryptedApiKey, p.EncryptedApiKey);
        Assert.Equal("sk-x", SecretProtector.Unprotect(p.EncryptedApiKey));
    }

    [Fact] public void Save同Id覆盖而非重复()
    {
        _svc.Save(Profile("a1"));
        _svc.Save(Profile("a1") with { Model = "deepseek-v4-pro" });
        Assert.Single(_svc.Profiles);
        Assert.Equal("deepseek-v4-pro", _svc.Profiles[0].Model);
    }

    [Fact] public void SetActive后GetActive返回该档案()
    {
        _svc.Save(Profile("a1")); _svc.Save(Profile("b2", "ollama"));
        _svc.SetActive("b2");
        Assert.Equal("b2", _svc.GetActive()!.Id);
        Assert.Equal("b2", _settings.Current.ActiveProfileId);
    }

    [Fact] public void 删除激活档案时ActiveProfileId被清空()
    {
        _svc.Save(Profile("a1"));
        _svc.SetActive("a1");
        _svc.Delete("a1");
        Assert.Null(_svc.GetActive());
        Assert.Null(_settings.Current.ActiveProfileId);
    }

    [Fact] public void Load文件不存在时为空列表不抛异常()
    {
        var svc = new LlmProfileService(Path.Combine(_dir, "none.json"), _settings);
        svc.Load();
        Assert.Empty(svc.Profiles);
    }
}
