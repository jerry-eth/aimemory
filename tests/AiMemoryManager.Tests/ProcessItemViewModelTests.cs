using AiMemoryManager.Models;
using AiMemoryManager.ViewModels;

namespace AiMemoryManager.Tests;

public class ProcessItemViewModelTests
{
    [Fact]
    public void 更新快照会保留行对象并通知实时字段()
    {
        var item = new ProcessItemViewModel
        {
            Snapshot = new ProcessSnapshot(42, "demo", @"C:\demo.exe", 20L << 20, false),
            IsCritical = false,
            CanKill = true
        };
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.UpdateSnapshot(
            new ProcessSnapshot(42, "demo", @"C:\demo.exe", 36L << 20, true),
            18.5);

        Assert.Equal(42, item.Pid);
        Assert.Equal("36 MB", item.MemoryText);
        Assert.Equal("18.5%", item.CpuText);
        Assert.Equal("前台", item.StatusText);
        Assert.Contains(nameof(ProcessItemViewModel.MemoryText), changed);
        Assert.Contains(nameof(ProcessItemViewModel.CpuText), changed);
        Assert.Contains(nameof(ProcessItemViewModel.StatusText), changed);
    }

    [Fact]
    public void 旧构造形式仍支持没有CPU样本的快照()
    {
        var snapshot = new ProcessSnapshot(1, "demo", null, 1L << 20, false);

        Assert.Equal(TimeSpan.Zero, snapshot.TotalProcessorTime);
    }
}
