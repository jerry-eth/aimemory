using AiMemoryManager.Models;
using AiMemoryManager.ViewModels;
using Xunit;

namespace AiMemoryManager.Tests;

/// <summary>
/// M1 走查发现的缺陷回归:进程列表实时刷新只原地更新数值、不重排,导致显示顺序随时间漂移;
/// 手动「刷新」应强制恢复内存降序(forceResort),实时刷新保持原地不动(防卡顿)。
/// </summary>
public class ProcessesViewModelSortTests
{
    private static ProcessesViewModel.ProcessRowData Row(int pid, long mb) =>
        new(new ProcessSnapshot(pid, $"p{pid}", null, mb << 20, false), false, false, true);

    [Fact]
    public void 实时刷新_原地更新不重排()
    {
        var vm = new ProcessesViewModel();
        vm.ApplyRows(new[] { Row(1, 100), Row(2, 90), Row(3, 80) }, forceResort: false);

        // C 涨到 120MB,实时刷新只更新数值,顺序保持 A,B,C
        vm.ApplyRows(new[] { Row(3, 120), Row(1, 100), Row(2, 90) }, forceResort: false);

        Assert.Equal(new[] { 1, 2, 3 }, vm.Items.Select(i => i.Pid).ToArray());
        Assert.Equal(120 << 20, vm.Items[2].Snapshot.WorkingSetBytes);
    }

    [Fact]
    public void 手动刷新_强制恢复内存降序()
    {
        var vm = new ProcessesViewModel();
        vm.ApplyRows(new[] { Row(1, 100), Row(2, 90), Row(3, 80) }, forceResort: false);
        vm.ApplyRows(new[] { Row(3, 120), Row(1, 100), Row(2, 90) }, forceResort: false);
        Assert.Equal(new[] { 1, 2, 3 }, vm.Items.Select(i => i.Pid).ToArray()); // 前置:已漂移

        vm.ApplyRows(new[] { Row(3, 120), Row(1, 100), Row(2, 90) }, forceResort: true);

        Assert.Equal(new[] { 3, 1, 2 }, vm.Items.Select(i => i.Pid).ToArray());
    }

    [Fact]
    public void 手动刷新_重排时保留选中状态()
    {
        var vm = new ProcessesViewModel();
        vm.ApplyRows(new[] { Row(1, 100), Row(2, 90), Row(3, 80) }, forceResort: false);
        vm.Items[2].IsSelected = true;   // 用户勾选了 C

        vm.ApplyRows(new[] { Row(3, 120), Row(1, 100), Row(2, 90) }, forceResort: true);

        Assert.Equal(new[] { 3, 1, 2 }, vm.Items.Select(i => i.Pid).ToArray());
        Assert.True(vm.Items[0].IsSelected);   // C 排到首位,勾选不丢
    }

    [Fact]
    public void 手动刷新_进程退出和新进程加入后仍有序()
    {
        var vm = new ProcessesViewModel();
        vm.ApplyRows(new[] { Row(1, 100), Row(2, 90), Row(3, 80) }, forceResort: false);

        // pid=2 退出,pid=4 新启动
        vm.ApplyRows(new[] { Row(3, 120), Row(1, 100), Row(4, 50) }, forceResort: true);

        Assert.Equal(new[] { 3, 1, 4 }, vm.Items.Select(i => i.Pid).ToArray());
    }
}
