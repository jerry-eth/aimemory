using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public sealed class CDriveCommercialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amm-commercial-" + Guid.NewGuid().ToString("N"));

    public CDriveCommercialTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void 本地规则无大模型也能生成缓存和迁移建议()
    {
        var temp = Path.Combine(_root, "TempChild");
        Directory.CreateDirectory(temp);
        File.WriteAllBytes(Path.Combine(temp, "cache.bin"), new byte[1024]);
        var userData = Path.Combine(_root, "Videos");
        Directory.CreateDirectory(userData);
        File.WriteAllBytes(Path.Combine(userData, "video.bin"), new byte[2048]);

        var service = new LocalDiskRuleService(new[] { "D:\\" });
        var advice = service.Generate(new[]
        {
            new FolderSizeInfo(temp, DiskCategory.Temp, 1024, 1),
            new FolderSizeInfo(userData, DiskCategory.UserFolder, 2048, 1),
        });

        Assert.Single(advice.Cleanable);
        Assert.Single(advice.Migratable);
        Assert.Equal(DiskAdviceSource.LocalRules, advice.Source);
    }

    [Fact]
    public async Task 详细扫描支持进度空间概览和取消结果()
    {
        var folder = Path.Combine(_root, "data");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "hello");
        var progress = new List<DiskScanProgress>();
        var result = await new DiskScanService().ScanDetailedAsync(
            new[] { new DiskCandidate(folder, DiskCategory.Temp) },
            new Progress<DiskScanProgress>(progress.Add));

        var item = Assert.Single(result.Items);
        Assert.Equal(5, item.SizeBytes);
        Assert.NotNull(result.Space);
        Assert.NotEmpty(progress);
        Assert.False(result.IsCanceled);
    }

    [Fact]
    public void 临时目录根本身不允许作为清理项()
    {
        Assert.True(PathSafetyService.IsProtectedForOperation(Path.GetTempPath(), DiskCategory.Temp, false));
        Assert.False(PathSafetyService.IsSafeCleanCandidate(Path.GetTempPath(), DiskCategory.Temp));
    }

    [Fact]
    public async Task 无激活档案时建议服务自动降级本地规则()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var profiles = new LlmProfileService(Path.Combine(_root, "profiles.json"), settings);
        profiles.Load();
        var l10n = new LocalizationService(Path.Combine(AppContext.BaseDirectory, "i18n"));
        var service = new DiskAdviceService(profiles, new NoopLlmClient(), l10n,
            new TokenStatsService(Path.Combine(_root, "usage.jsonl"), () => DateTimeOffset.Now), new[] { "D:\\" });
        var folder = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(folder);

        var advice = await service.AnalyzeWithFallbackAsync(new[]
        {
            new FolderSizeInfo(folder, DiskCategory.Temp, 10 * 1024, 1)
        });

        Assert.Equal(DiskAdviceSource.LocalFallback, advice.Source);
        Assert.Single(advice.Cleanable);
    }

    private sealed class NoopLlmClient : ILlmClient
    {
        public Task<LlmResponse> ChatAsync(LlmProfile profile, string system, string user, CancellationToken ct = default)
            => throw new InvalidOperationException("不应调用 LLM");
        public Task<IReadOnlyList<string>> ListModelsAsync(LlmProfile profile, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    [Fact]
    public void 路径规范化保留磁盘根并拒绝模糊盘符()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(root, PathSafetyService.Normalize(root));
        Assert.Null(PathSafetyService.Normalize(root.TrimEnd(Path.DirectorySeparatorChar)));
    }
}



