using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Native;
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class AnalysisServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNativeMemoryApi _native;
    private readonly WhitelistService _wl;
    private readonly SettingsService _settings;
    private readonly LlmProfileService _profiles;
    private readonly PromptTemplateService _prompts;
    private readonly AnalysisCacheService _cache;
    private readonly TokenStatsService _stats;
    private readonly FakeLlmClient _client;
    private readonly AnalysisService _svc;
    private DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeLlmClient : ILlmClient
    {
        public int Calls;
        public string LastUserPrompt = "";
        public string LastSystemPrompt = "";
        // 偏差说明:brief 的原始字符串跨行写法不是合法 C#(多行 raw string 的 """ 必须独占行),
        // 改为单行 raw string,JSON 内容逐字不变。
        public string Reply = """{"suggestions":[{"process":"chrome","action":"compress","reason":"占用高","risk":"low"},{"process":"csrss","action":"compress","reason":"模型瞎说","risk":"low"},{"process":"myapp","action":"compress","reason":"白名单内","risk":"low"}]}""";
        public Task<LlmResponse> ChatAsync(LlmProfile profile, string systemPrompt, string userPrompt, CancellationToken ct = default)
        { Calls++; LastSystemPrompt = systemPrompt; LastUserPrompt = userPrompt; return Task.FromResult(new LlmResponse(Reply, new LlmUsage(100, 50))); }
        public Task<IReadOnlyList<string>> ListModelsAsync(LlmProfile profile, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "m1" });
    }

    public AnalysisServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(Path.Combine(_dir, "s.json"));
        _settings.Load();
        _native = new FakeNativeMemoryApi
        {
            Processes =
            {
                new(1, "chrome", @"C:\chrome.exe", 900L << 20, true),
                new(2, "csrss", null, 200L << 20, false),
                new(3, "myapp", null, 300L << 20, true),
            },
            ForegroundPid = -1
        };
        _wl = new WhitelistService(_settings);
        _wl.Add("myapp");
        _profiles = new LlmProfileService(Path.Combine(_dir, "p.json"), _settings);
        _profiles.Load();
        _profiles.Save(new LlmProfile
        {
            Id = "p1", Name = "ds", BaseUrl = "https://api.test/v1",
            EncryptedApiKey = SecretProtector.Protect("sk"), Model = "m1"
        });
        _profiles.SetActive("p1");
        _prompts = new PromptTemplateService(Path.Combine(_dir, "t.json"));
        _prompts.Load();
        _cache = new AnalysisCacheService(Path.Combine(_dir, "c.json"), () => _now);
        _stats = new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => _now);
        _client = new FakeLlmClient();
        var l10n = new LocalizationService(I18nDir);
        _svc = new AnalysisService(_native, _wl, new ForegroundGuard(_native, () => 999),
            _profiles, _prompts, _client, _cache, _stats, _settings, l10n);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    // 偏差说明:brief 用相对路径回溯到 src/.../Assets/i18n;此处沿用仓库既有约定
    // (同 LocalizationServiceTests),i18n/*.json 由测试 csproj 复制到输出目录。
    private static string I18nDir => Path.Combine(AppContext.BaseDirectory, "i18n");

    [Fact] public async Task 建议经系统关键与白名单硬过滤()
    {
        var r = await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        var s = Assert.Single(r.Suggestions);          // 模型给了 3 条,只剩 chrome
        Assert.Equal("chrome", s.ProcessName);
        Assert.False(r.FromCache);
    }

    [Fact] public async Task Token用量被记录()
    {
        await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        var all = _stats.LoadAll();
        Assert.Single(all);
        Assert.Equal(100, all[0].InputTokens);
        Assert.Equal(AnalysisTrigger.Manual, all[0].Trigger);
    }

    [Fact] public async Task 第二次相同快照命中缓存不调LLM()
    {
        await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        var r2 = await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        Assert.Equal(1, _client.Calls);
        Assert.True(r2.FromCache);
    }

    [Fact] public async Task 无激活档案时抛明确异常()
    {
        _profiles.Delete("p1");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.AnalyzeAsync(AnalysisTrigger.Manual));
        Assert.Contains("档案", ex.Message);
    }

    [Fact] public async Task 提示词包含进程JSON且不含白名单进程()
    {
        await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        Assert.Contains("chrome", _client.LastUserPrompt);
        Assert.DoesNotContain("myapp", _client.LastUserPrompt);   // 白名单进程不发给 LLM
        Assert.DoesNotContain("csrss", _client.LastUserPrompt);   // 系统关键不发给 LLM
    }

    [Fact] public async Task AnalysisCompleted事件触发()
    {
        AnalysisResult? got = null;
        _svc.AnalysisCompleted += (_, r) => got = r;
        await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        Assert.NotNull(got);
    }

    [Fact] public async Task 系统提示词占位符被替换()
    {
        _prompts.Save(new PromptTemplate
        {
            Id = "custom", Name = "c", IsDefault = true,
            Content = "mem={memory_info}|custom={custom_instructions}|lang={language}|{process_list}"
        });
        _settings.Current.CustomInstructions = "别动游戏";
        await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        Assert.DoesNotContain("{memory_info}", _client.LastSystemPrompt);
        Assert.DoesNotContain("{process_list}", _client.LastSystemPrompt);
        Assert.DoesNotContain("{custom_instructions}", _client.LastSystemPrompt);
        Assert.DoesNotContain("{language}", _client.LastSystemPrompt);
        Assert.Contains("别动游戏", _client.LastSystemPrompt);
        Assert.Contains("chrome", _client.LastSystemPrompt);      // 进程 JSON 已注入系统提示词
        Assert.Contains("lang=中文", _client.LastSystemPrompt);
    }

    [Fact] public async Task forceRefresh跳过缓存重新调LLM但仍写缓存()
    {
        await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        var r2 = await _svc.AnalyzeAsync(AnalysisTrigger.Manual, forceRefresh: true);
        Assert.Equal(2, _client.Calls);                            // 缓存被跳过
        Assert.False(r2.FromCache);
        var r3 = await _svc.AnalyzeAsync(AnalysisTrigger.Manual);  // 写入了缓存,普通调用复用
        Assert.Equal(2, _client.Calls);
        Assert.True(r3.FromCache);
    }

    [Fact] public async Task 自定义指令变化使缓存失效()
    {
        await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        _settings.Current.CustomInstructions = "别动游戏";
        await _svc.AnalyzeAsync(AnalysisTrigger.Manual);
        Assert.Equal(2, _client.Calls);                            // 指令变化 → 哈希变化 → 重新请求
    }
}
