using AiMemoryManager.Models;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Tests;

public class SmartAnalysisSuggestionTests
{
    [Fact]
    public void 可执行的低风险建议默认勾选_高风险建议默认不勾选()
    {
        var compress = new SuggestionItemViewModel
        {
            Suggestion = new AnalysisSuggestion("notepad", "compress", "工作集较大", "low")
        };
        var terminate = new SuggestionItemViewModel
        {
            Suggestion = new AnalysisSuggestion("unknown", "terminate", "长期占用", "high")
        };

        Assert.True(compress.CanExecute);
        Assert.True(compress.IsDefaultSelected);
        Assert.True(terminate.CanExecute);
        Assert.False(terminate.IsDefaultSelected);
    }

    [Fact]
    public void 保留建议不可执行且默认不勾选()
    {
        var keep = new SuggestionItemViewModel
        {
            Suggestion = new AnalysisSuggestion("explorer", "keep", "系统进程", "low")
        };

        Assert.False(keep.CanExecute);
        Assert.False(keep.IsDefaultSelected);
    }
}
