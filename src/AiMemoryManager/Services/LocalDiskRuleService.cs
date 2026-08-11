using System.IO;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

/// <summary>无需网络和 API Key 的本地瘦身规则。</summary>
public sealed class LocalDiskRuleService
{
    private readonly IReadOnlyList<string> _availableDrives;

    public LocalDiskRuleService(IReadOnlyList<string> availableDrives) => _availableDrives = availableDrives;

    public DiskAdvice Generate(IReadOnlyList<FolderSizeInfo> scan)
    {
        var cleanable = new List<DiskCleanableItem>();
        var migratable = new List<DiskMigratableItem>();
        foreach (var item in scan.Where(i => i.SizeBytes > 0))
        {
            if (item.Category is DiskCategory.Temp or DiskCategory.BrowserCache &&
                PathSafetyService.IsSafeCleanCandidate(item.Path, item.Category))
            {
                var reason = item.Category == DiskCategory.BrowserCache
                    ? "浏览器缓存，可删除后自动重建"
                    : "临时文件，建议先关闭相关程序后移入回收站";
                cleanable.Add(new DiskCleanableItem(item.Path, reason, item.SizeBytes)
                {
                    Category = item.Category,
                    Risk = item.IsPartial ? "Review" : "Low"
                });
                continue;
            }

            if (item.Category == DiskCategory.UserFolder &&
                PathSafetyService.IsSafeMigrationCandidate(item.Path, item.Category) &&
                _availableDrives.Count > 0)
            {
                var target = ChooseTarget(item.Path);
                if (target is not null)
                {
                    migratable.Add(new DiskMigratableItem(item.Path,
                        "用户数据占用空间较大，迁移前请关闭正在使用它的程序并确认目标盘",
                        target)
                    {
                        Category = item.Category,
                        Risk = "Review"
                    });
                }
            }
        }
        return new DiskAdvice(cleanable, migratable)
        {
            Source = DiskAdviceSource.LocalRules,
            StatusMessage = "已使用本地规则生成建议"
        };
    }

    private string? ChooseTarget(string source)
    {
        var sourceRoot = Path.GetPathRoot(source);
        return _availableDrives
            .Select(d => d.TrimEnd('\\') + "\\")
            .Where(d => !string.Equals(Path.GetPathRoot(d), sourceRoot, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }
}


