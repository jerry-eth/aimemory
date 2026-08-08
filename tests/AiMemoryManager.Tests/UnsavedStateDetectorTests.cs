using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class UnsavedStateDetectorTests
{
    // 注:Task 1 的 FakeNativeMemoryApi 用 Dictionary<int,List<string>> WindowTitles,
    // 无 SetWindowTitles 方法,故按 brief 指引最小调整为直接操作字典。
    [Fact] public void 标题含星号判为未保存()
    {
        var fake = new FakeNativeMemoryApi();
        fake.WindowTitles[1] = new List<string> { "无标题 - 记事本*", "其他窗口" };
        Assert.True(new UnsavedStateDetector(fake).HasUnsavedSigns(1));
    }

    [Fact] public void 标题含圆点判为未保存()
    {
        var fake = new FakeNativeMemoryApi();
        fake.WindowTitles[1] = new List<string> { "文档• - Word" };
        Assert.True(new UnsavedStateDetector(fake).HasUnsavedSigns(1));
    }

    [Fact] public void 正常标题不误报()
    {
        var fake = new FakeNativeMemoryApi();
        fake.WindowTitles[1] = new List<string> { "文档 - Word", "设置" };
        Assert.False(new UnsavedStateDetector(fake).HasUnsavedSigns(1));
    }

    [Fact] public void 无窗口进程不误报()
    {
        var fake = new FakeNativeMemoryApi();
        Assert.False(new UnsavedStateDetector(fake).HasUnsavedSigns(999));
    }
}
