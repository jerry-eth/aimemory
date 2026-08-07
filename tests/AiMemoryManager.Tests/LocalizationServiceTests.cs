using System.IO;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class LocalizationServiceTests
{
    private static string I18nDir => Path.Combine(AppContext.BaseDirectory, "i18n");

    [Fact] public void 按键取词_缺key返回key本身()
    {
        var l = new LocalizationService(I18nDir);
        l.CurrentLanguage = "zh-CN";
        Assert.Equal("AI 内存管家", l["App.Title"]);
        Assert.Equal("No.Such.Key", l["No.Such.Key"]);
    }

    [Fact] public void 切换语言后取词即时变化()
    {
        var l = new LocalizationService(I18nDir);
        l.CurrentLanguage = "en";
        Assert.Equal("Clean Now", l["Dashboard.CleanL1"]);
        l.CurrentLanguage = "zh-CN";
        Assert.Equal("一键清理", l["Dashboard.CleanL1"]);
    }

    [Fact] public void 切换语言触发Item索引器变更通知()
    {
        var l = new LocalizationService(I18nDir);
        var changed = new List<string?>();
        l.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        l.CurrentLanguage = "en";
        Assert.Contains("Item[]", changed);
    }
}
