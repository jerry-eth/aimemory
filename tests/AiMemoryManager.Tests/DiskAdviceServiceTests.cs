using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class DiskAdviceServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private readonly DiskAdviceService _svc;

    private sealed class FakeClient : ILlmClient
    {
        public string Reply = """
            {"cleanable":[{"path":"C:\\Users\\j\\AppData\\Local\\Temp","reason":"临时文件","estMB":1200},
                          {"path":"C:\\Windows\\System32","reason":"模型瞎说","estMB":1}],
             "migratable":[{"path":"C:\\Users\\j\\Videos","reason":"视频库大","target":"D:"},
                          {"path":"C:\\Users\\j\\NotInScan","reason":"不在扫描里","target":"D:"},
                          {"path":"C:\\Users\\j\\Games","reason":"游戏库","target":"Z:"}]}
            """;
        public string LastPrompt = "";
        public Task<LlmResponse> ChatAsync(LlmProfile profile, string system, string user, CancellationToken ct = default)
        { LastPrompt = user; return Task.FromResult(new LlmResponse(Reply, new LlmUsage(500, 200))); }
        public Task<IReadOnlyList<string>> ListModelsAsync(LlmProfile profile, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    public DiskAdviceServiceTests()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsService(Path.Combine(_dir, "s.json"));
        settings.Load();
        var profiles = new LlmProfileService(Path.Combine(_dir, "p.json"), settings);
        profiles.Load();
        profiles.Save(new LlmProfile { Id = "p1", Name = "ds", BaseUrl = "https://x/v1", Model = "m" });
        profiles.SetActive("p1");
        var l10n = new LocalizationService(Path.Combine(AppContext.BaseDirectory, "i18n"));
        _svc = new DiskAdviceService(profiles, _client, l10n,
            new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => DateTimeOffset.Now),
            new[] { "D:\\", "E:\\" });
    }
    public void Dispose() => Directory.Delete(_dir, true);

    // 偏差说明:brief 按 (path, size, count, category) 位置传参,但 Task 9 落地的
    // FolderSizeInfo 记录签名为 (Path, Category, SizeBytes, FileCount),此处改用命名参数。
    private static IReadOnlyList<FolderSizeInfo> Scan() => new[]
    {
        new FolderSizeInfo(@"C:\Users\j\AppData\Local\Temp", DiskCategory.Temp, 1200L << 20, 500),
        new FolderSizeInfo(@"C:\Users\j\Videos", DiskCategory.UserFolder, 50L << 30, 100),
        new FolderSizeInfo(@"C:\Users\j\Games", DiskCategory.UserFolder, 80L << 30, 200),
        new FolderSizeInfo(@"C:\Windows\System32", DiskCategory.Other, 1L << 30, 999),
    };

    [Fact] public async Task 建议经系统路径与候选集双重过滤()
    {
        var a = await _svc.AnalyzeAsync(Scan());
        var c = Assert.Single(a.Cleanable);          // Windows\System32 被滤
        Assert.Contains("Temp", c.Path);
        Assert.Equal(1200L << 20, c.EstBytes);
        var m = Assert.Single(a.Migratable);         // NotInScan(不在候选)与 Z:(盘不存在)被滤
        Assert.Equal(@"C:\Users\j\Videos", m.Path);
        Assert.Equal("D:", m.TargetDrive);
    }

    [Fact] public async Task Token被记录()
    {
        await _svc.AnalyzeAsync(Scan());
        var stats = new TokenStatsService(Path.Combine(_dir, "u.jsonl"), () => DateTimeOffset.Now);
        Assert.Equal(500, stats.LoadAll().Sum(r => r.InputTokens));
    }

    [Fact] public async Task 无激活档案抛明确异常()
    {
        var settings = new SettingsService(Path.Combine(_dir, "s2.json"));
        settings.Load();
        var empty = new LlmProfileService(Path.Combine(_dir, "p2.json"), settings);
        empty.Load();
        var svc = new DiskAdviceService(empty, _client,
            new LocalizationService(Path.Combine(AppContext.BaseDirectory, "i18n")),
            new TokenStatsService(Path.Combine(_dir, "u2.jsonl"), () => DateTimeOffset.Now),
            new[] { "D:\\" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AnalyzeAsync(Scan()));
    }

    [Fact] public async Task 坏输出返回空建议不抛()
    {
        _client.Reply = "not json";
        var a = await _svc.AnalyzeAsync(Scan());
        Assert.Empty(a.Cleanable);
        Assert.Empty(a.Migratable);
    }

    [Fact] public async Task 提示词包含扫描数据与可用盘符()
    {
        await _svc.AnalyzeAsync(Scan());
        Assert.Contains("Videos", _client.LastPrompt);
        Assert.Contains("D:\\", _client.LastPrompt);
    }
}
