using System.Windows.Input;
using AiMemoryManager.ViewModels;
using Xunit;

namespace AiMemoryManager.Tests;

/// <summary>
/// 恢复按钮无响应缺陷回归(2026-08-21):[RelayCommand] 方法 RestoreAsync 是同步方法,
/// 源生成器只对返回 Task 的异步方法剥掉 Async 后缀,实际生成的是 RestoreAsyncCommand,
/// XAML 却绑定 RestoreCommand → 绑定 PathError、Command=null,点击静默无效。
/// 此类「XAML 命令名与生成名不一致」的故障运行期无任何异常,用命名契约测试兜底。
/// </summary>
public class ViewModelCommandNamingTests
{
    [Fact]
    public void ProcessesViewModel_必须暴露_XAML_绑定的命令名()
    {
        var t = typeof(ProcessesViewModel);
        foreach (var name in new[] { "RefreshCommand", "TerminateSelectedCommand", "RestoreCommand", "AddToWhitelistCommand" })
        {
            var prop = t.GetProperty(name);
            Assert.True(prop != null, $"{t.Name} 缺少命令属性 {name}(XAML 绑定将静默失效)");
            Assert.True(typeof(ICommand).IsAssignableFrom(prop!.PropertyType), $"{name} 必须实现 ICommand");
        }
    }
}
