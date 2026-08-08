using System.IO;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class SystemPathGuardTests
{
    private static string Sys => Environment.GetFolderPath(Environment.SpecialFolder.System)
        is var s ? Path.GetPathRoot(s)! : @"C:\";   // 系统盘根,如 "C:\"

    [Fact] public void Windows目录及子路径受保护()
    {
        Assert.True(SystemPathGuard.IsProtected(Sys + @"Windows"));
        Assert.True(SystemPathGuard.IsProtected(Sys + @"Windows\System32"));
        Assert.True(SystemPathGuard.IsProtected(Sys + @"windows\temp"));   // 大小写不敏感
    }

    [Fact] public void ProgramFiles与ProgramData受保护()
    {
        Assert.True(SystemPathGuard.IsProtected(Sys + "Program Files"));
        Assert.True(SystemPathGuard.IsProtected(Sys + "Program Files (x86)"));
        Assert.True(SystemPathGuard.IsProtected(Sys + @"ProgramData\Microsoft"));
    }

    [Fact] public void 用户目录不受保护()
    {
        Assert.False(SystemPathGuard.IsProtected(Sys + @"Users\jerry\Downloads"));
        Assert.False(SystemPathGuard.IsProtected(@"D:\Games"));
    }

    [Fact] public void 前缀撞名不误判()
    {
        Assert.False(SystemPathGuard.IsProtected(Sys + @"WindowsOld"));       // 不是 Windows\
        Assert.False(SystemPathGuard.IsProtected(Sys + @"Program Files2"));   // 不是 Program Files\
    }
}
