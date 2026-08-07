# M1 基础版实现计划 — AI 内存管家

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建 WPF 内存管理工具的基础版:实时监控、L1/L2 清理、规则引擎、白名单、托盘、中英文切换、Fluent 界面。

**Architecture:** MVVM(CommunityToolkit.Mvvm)+ 服务层。所有 Win32/P-Invoke 收敛在 `INativeMemoryApi` 接口背后,业务服务(Settings/Whitelist/Rule/Clean/Localization)全部可单元测试;UI 只做绑定。L2 清理通过"一次性 UAC 注册计划任务 + 后续静默触发提权 Helper"实现免重复弹窗。

**Tech Stack:** .NET 8(net8.0-windows)、WPF、WPF-UI(lepoco/wpfui)、CommunityToolkit.Mvvm、H.NotifyIcon.Wpf、xUnit。

## Global Constraints

- 目标框架 `net8.0-windows`,x64,自包含发布;安装包目标 ≤ 40MB
- 配置文件路径:`%APPDATA%\AiMemoryManager\settings.json`,严禁写入安装目录
- L1 清理只允许 `EmptyWorkingSet`,**绝不杀进程**
- 系统关键进程硬名单(`WhitelistService.IsSystemCritical`)不可被任何清理触及
- 界面默认跟随系统语言(zh-CN / en),切换即时生效不重启
- commit 信息用中文;严禁提交 `.env`/密钥
- 每个服务类一个文件,单一职责;ViewModel 不直接 P/Invoke

## Scope Clarification(与 spec 的口径)

- FR-2.6/FR-2.7(未保存检测、后悔药)仅作用于 L3 杀进程,L3 在 M3 → **本计划不含**,M3 计划时实现。
- FR-1.3 迷你曲线为 P1,本计划包含(仪表盘 Polyline 自绘,无第三方图表库,保体积)。

---

## File Structure

```
AiMemoryManager.sln
src/
  AiMemoryManager/                          WPF 主程序
    AiMemoryManager.csproj
    App.xaml / App.xaml.cs                  启动、DI 容器、单实例
    MainWindow.xaml / .cs                   FluentWindow + Mica + 导航
    Controls/PercentageRing.xaml/.cs        仪表盘圆环控件
    Native/NativeMemoryApi.cs               INativeMemoryApi 的真实实现(P/Invoke)
    Native/INativeMemoryApi.cs              接口
    Models/ProcessSnapshot.cs               记录类型
    Models/SystemMemoryInfo.cs
    Models/CleanResult.cs / CleanLevel.cs / CleanTrigger.cs
    Models/AppSettings.cs
    Services/SettingsService.cs
    Services/WhitelistService.cs
    Services/CleanService.cs
    Services/ElevatedL2Service.cs           计划任务注册/触发/结果读取
    Services/RuleEngine.cs
    Services/ForegroundGuard.cs
    Services/LocalizationService.cs
    Services/MemoryMonitorService.cs        定时采样 + 事件
    Services/TrayIconRenderer.cs            动态托盘图标(内存百分比)
    ViewModels/*ViewModel.cs                Dashboard/Processes/Rules/Whitelist/Settings
    Views/*Page.xaml(.cs)                   五个页面
    Assets/i18n/en.json / zh-CN.json
  AiMemoryManager.ElevatedHelper/           L2 提权控制台程序
    AiMemoryManager.ElevatedHelper.csproj
    Program.cs                              --install / --purge-standby
tests/
  AiMemoryManager.Tests/
    AiMemoryManager.Tests.csproj
    Fakes/FakeNativeMemoryApi.cs / FakeClock.cs
    SettingsServiceTests.cs
    WhitelistServiceTests.cs
    RuleEngineTests.cs
    CleanServiceTests.cs
    LocalizationServiceTests.cs
```

**关键接口(后续任务依赖,签名不得改):**

```csharp
// Native/INativeMemoryApi.cs
public interface INativeMemoryApi
{
    SystemMemoryInfo GetSystemMemory();
    IReadOnlyList<ProcessSnapshot> GetProcessSnapshots();
    long EmptyWorkingSets(IReadOnlyCollection<int> pids); // 返回估算释放字节数
    int GetForegroundPid();
    bool IsFullscreenAppActive();
}

// Models
public record ProcessSnapshot(int Pid, string Name, string? Path, long WorkingSetBytes, bool HasVisibleWindow);
public record SystemMemoryInfo(long TotalBytes, long AvailableBytes)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : (1.0 - (double)AvailableBytes / TotalBytes) * 100;
}
public enum CleanLevel { L1, L2 }
public enum CleanTrigger { Manual, RuleThreshold, RuleTimer, Tray }
public record CleanResult(DateTimeOffset Time, CleanLevel Level, long FreedBytes, int ProcessCount, CleanTrigger Trigger);

// Services/SettingsService.cs
public class AppSettings
{
    public string Language { get; set; } = "auto";            // "auto" | "zh-CN" | "en"
    public double ThresholdPercent { get; set; } = 80;
    public int SustainSeconds { get; set; } = 30;
    public bool ThresholdRuleEnabled { get; set; } = false;
    public bool TimerRuleEnabled { get; set; } = false;
    public int TimerIntervalMinutes { get; set; } = 60;
    public bool AutoCleanIncludeL2 { get; set; } = false;
    public bool OnlyWhenNotFullscreen { get; set; } = true;
    public bool AnimationsEnabled { get; set; } = true;
    public List<string> ExcludedProcesses { get; set; } = new();
}
public class SettingsService
{
    public AppSettings Current { get; }
    public void Load();   // 文件不存在则用默认值并落盘
    public void Save();   // 缩进 JSON 写回
    public event EventHandler? SettingsSaved;
}

// Services/WhitelistService.cs
public class WhitelistService
{
    public IReadOnlyCollection<string> Excluded { get; }      // 小写进程名,不含 .exe
    public bool IsExcluded(string processName);
    public bool IsSystemCritical(string processName);          // 内置硬名单
    public void Add(string processName);                       // 自动小写、去 .exe、去重
    public void Remove(string processName);
    public void Import(string filePath);                       // 每行一个进程名
    public void Export(string filePath);
}

// Services/RuleEngine.cs
public record CleanRequest(CleanLevel Level, CleanTrigger Trigger);
public class RuleEngine
{
    public RuleEngine(SettingsService settings, INativeMemoryApi native, ForegroundGuard guard, Func<DateTimeOffset> clock);
    public event EventHandler<CleanRequest>? CleanRequested;
    public void Tick();                                        // 由 10s 定时器驱动
}

// Services/CleanService.cs
public class CleanService
{
    public CleanService(INativeMemoryApi native, WhitelistService whitelist, ElevatedL2Service l2, ForegroundGuard guard);
    public Task<CleanResult> RunL1Async(CleanTrigger trigger, CancellationToken ct = default);
    public Task<CleanResult> RunL2Async(CleanTrigger trigger, CancellationToken ct = default);
    public event EventHandler<CleanResult>? CleanCompleted;
}

// Services/LocalizationService.cs
public class LocalizationService : INotifyPropertyChanged
{
    public string this[string key] { get; }                    // 缺 key 返回 key 本身
    public string CurrentLanguage { get; set; }                // setter 内刷新字典并触发 Item[] 变更
    public void SetAuto();                                     // 跟随系统:zh 系→zh-CN,否则 en
}
```

---

### Task 1: 解决方案脚手架

**Files:**
- Create: `AiMemoryManager.sln`, `src/AiMemoryManager/AiMemoryManager.csproj`, `src/AiMemoryManager/App.xaml(.cs)`, `src/AiMemoryManager/MainWindow.xaml(.cs)`, `tests/AiMemoryManager.Tests/AiMemoryManager.Tests.csproj`, `src/AiMemoryManager.ElevatedHelper/AiMemoryManager.ElevatedHelper.csproj`

**Interfaces:**
- Produces: 可编译运行的空 WPF 窗口;`dotnet test` 可运行。

- [ ] **Step 1: 创建解决方案与三个项目**

```bash
cd C:/Users/jerry/Desktop/memory
dotnet new sln -n AiMemoryManager
dotnet new wpf -n AiMemoryManager -o src/AiMemoryManager -f net8.0
dotnet new console -n AiMemoryManager.ElevatedHelper -o src/AiMemoryManager.ElevatedHelper -f net8.0
dotnet new xunit -n AiMemoryManager.Tests -o tests/AiMemoryManager.Tests -f net8.0
dotnet sln add src/AiMemoryManager src/AiMemoryManager.ElevatedHelper tests/AiMemoryManager.Tests
dotnet add tests/AiMemoryManager.Tests reference src/AiMemoryManager
dotnet add src/AiMemoryManager package WPF-UI
dotnet add src/AiMemoryManager package CommunityToolkit.Mvvm
dotnet add src/AiMemoryManager package H.NotifyIcon.Wpf
dotnet add src/AiMemoryManager package System.Drawing.Common
```

- [ ] **Step 2: 修正目标框架标识**

WPF 模板生成的是 `net8.0-windows`,确认三个 csproj 中主程序与测试项目为:

```xml
<!-- src/AiMemoryManager/AiMemoryManager.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <AssemblyName>AiMemoryManager</AssemblyName>
    <RootNamespace>AiMemoryManager</RootNamespace>
  </PropertyGroup>
</Project>
```

测试项目 `TargetFramework` 改为 `net8.0-windows` 并加 `<UseWPF>true</UseWPF>`(引用 WPF 程序集需要)。

- [ ] **Step 3: 构建验证**

Run: `dotnet build AiMemoryManager.sln`
Expected: `Build succeeded`,0 error(WPF-UI 版本以 NuGet 最新稳定版为准,若 API 与后文任务不一致,以包内 IntelliSense/官方 repo 示例为准调整)。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "脚手架:解决方案、WPF主程序、提权Helper、xUnit测试项目"
```

---

### Task 2: INativeMemoryApi 与原生实现

**Files:**
- Create: `src/AiMemoryManager/Native/INativeMemoryApi.cs`, `src/AiMemoryManager/Native/NativeMemoryApi.cs`, `src/AiMemoryManager/Models/ProcessSnapshot.cs`, `src/AiMemoryManager/Models/SystemMemoryInfo.cs`
- Test: `tests/AiMemoryManager.Tests/Fakes/FakeNativeMemoryApi.cs`

**Interfaces:**
- Produces: 头部"关键接口"中定义的 `INativeMemoryApi`、`ProcessSnapshot`、`SystemMemoryInfo`、`FakeNativeMemoryApi`(后续所有测试依赖)。

- [ ] **Step 1: 写接口与模型**

`Models/ProcessSnapshot.cs`:
```csharp
namespace AiMemoryManager.Models;
public record ProcessSnapshot(int Pid, string Name, string? Path, long WorkingSetBytes, bool HasVisibleWindow);
```

`Models/SystemMemoryInfo.cs`:
```csharp
namespace AiMemoryManager.Models;
public record SystemMemoryInfo(long TotalBytes, long AvailableBytes)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : (1.0 - (double)AvailableBytes / TotalBytes) * 100;
}
```

`Native/INativeMemoryApi.cs` 按"关键接口"签名,namespace `AiMemoryManager.Native`。

- [ ] **Step 2: 写 Fake(供测试使用)**

`tests/.../Fakes/FakeNativeMemoryApi.cs`:
```csharp
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Tests.Fakes;

public class FakeNativeMemoryApi : INativeMemoryApi
{
    public SystemMemoryInfo Memory { get; set; } = new(16L << 30, 8L << 30);
    public List<ProcessSnapshot> Processes { get; set; } = new();
    public int ForegroundPid { get; set; } = -1;
    public bool FullscreenActive { get; set; } = false;
    public List<int> EmptiedPids { get; } = new();
    public long FreedPerCall { get; set; } = 100L << 20;

    public SystemMemoryInfo GetSystemMemory() => Memory;
    public IReadOnlyList<ProcessSnapshot> GetProcessSnapshots() => Processes;
    public long EmptyWorkingSets(IReadOnlyCollection<int> pids)
    {
        EmptiedPids.AddRange(pids);
        return FreedPerCall;
    }
    public int GetForegroundPid() => ForegroundPid;
    public bool IsFullscreenAppActive() => FullscreenActive;
}
```

- [ ] **Step 3: 写真实实现**

`Native/NativeMemoryApi.cs`:
```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AiMemoryManager.Models;

namespace AiMemoryManager.Native;

public class NativeMemoryApi : INativeMemoryApi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int pquns);

    public SystemMemoryInfo GetSystemMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SystemMemoryInfo((long)m.ullTotalPhys, (long)m.ullAvailPhys);
    }

    public IReadOnlyList<ProcessSnapshot> GetProcessSnapshots()
    {
        var list = new List<ProcessSnapshot>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* 无权限读路径 */ }
                    list.Add(new ProcessSnapshot(
                        p.Id, p.ProcessName, path, p.WorkingSet64,
                        p.MainWindowHandle != IntPtr.Zero));
                }
                catch { /* 进程已退出,跳过 */ }
            }
        }
        return list;
    }

    public long EmptyWorkingSets(IReadOnlyCollection<int> pids)
    {
        long freed = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                long before = p.WorkingSet64;
                if (EmptyWorkingSet(p.Handle))
                {
                    p.Refresh();
                    long after = p.WorkingSet64;
                    if (before > after) freed += before - after;
                }
            }
            catch { /* 进程退出或无权限,跳过 */ }
        }
        return freed;
    }

    public int GetForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return -1;
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    public bool IsFullscreenAppActive()
    {
        // QUNS_BUSY=2, QUNS_RUNNING_D3D_FULL_SCREEN=3
        return SHQueryUserNotificationState(out int state) == 0 && (state == 2 || state == 3);
    }
}
```

- [ ] **Step 4: 构建 + Commit**

Run: `dotnet build AiMemoryManager.sln` → 0 error
```bash
git add -A
git commit -m "原生层:INativeMemoryApi 接口、Win32 实现与测试 Fake"
```

---

### Task 3: SettingsService(TDD)

**Files:**
- Create: `src/AiMemoryManager/Models/AppSettings.cs`, `src/AiMemoryManager/Services/SettingsService.cs`
- Test: `tests/AiMemoryManager.Tests/SettingsServiceTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `AppSettings`、`SettingsService`(签名见头部)

- [ ] **Step 1: 写失败测试**

```csharp
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    public SettingsServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Load_无配置文件时使用默认值并落盘()
    {
        var path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path);
        svc.Load();
        Assert.Equal(80, svc.Current.ThresholdPercent);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_后再Load_配置保持一致()
    {
        var path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path);
        svc.Load();
        svc.Current.ThresholdPercent = 66;
        svc.Current.ExcludedProcesses.Add("chrome");
        svc.Save();

        var svc2 = new SettingsService(path);
        svc2.Load();
        Assert.Equal(66, svc2.Current.ThresholdPercent);
        Assert.Contains("chrome", svc2.Current.ExcludedProcesses);
    }

    [Fact]
    public void Load_配置文件损坏时回退默认值()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json !!");
        var svc = new SettingsService(path);
        svc.Load();
        Assert.Equal(80, svc.Current.ThresholdPercent);
    }

    [Fact]
    public void Save_触发SettingsSaved事件()
    {
        var svc = new SettingsService(Path.Combine(_dir, "settings.json"));
        svc.Load();
        bool fired = false;
        svc.SettingsSaved += (_, _) => fired = true;
        svc.Save();
        Assert.True(fired);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/AiMemoryManager.Tests --filter SettingsServiceTests`
Expected: 编译失败 `SettingsService 不存在`

- [ ] **Step 3: 实现**

`Models/AppSettings.cs` 按头部定义(namespace `AiMemoryManager.Models`)。

`Services/SettingsService.cs`:
```csharp
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    public AppSettings Current { get; private set; } = new();
    public event EventHandler? SettingsSaved;

    public SettingsService(string path) => _path = path;

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "settings.json");

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
                Current.ExcludedProcesses ??= new();
                return;
            }
        }
        catch { /* 损坏 → 默认值 */ }
        Current = new AppSettings();
        Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOpts));
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/AiMemoryManager.Tests --filter SettingsServiceTests`
Expected: 4 passed

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "设置服务:JSON 读写、默认值回退、损坏容错(TDD)"
```

---

### Task 4: WhitelistService(TDD)

**Files:**
- Create: `src/AiMemoryManager/Services/WhitelistService.cs`
- Test: `tests/AiMemoryManager.Tests/WhitelistServiceTests.cs`

**Interfaces:**
- Consumes: `SettingsService`(ExcludedProcesses 持久化)
- Produces: 头部定义的 `WhitelistService` 签名

- [ ] **Step 1: 写失败测试**

```csharp
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class WhitelistServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly WhitelistService _wl;

    public WhitelistServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        _settings.Load();
        _wl = new WhitelistService(_settings);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void Add_自动小写并去掉exe后缀()
    {
        _wl.Add("Chrome.EXE");
        Assert.Contains("chrome", _wl.Excluded);
        Assert.True(_wl.IsExcluded("CHROME"));
    }

    [Fact] public void Add_重复添加只保留一份()
    {
        _wl.Add("code"); _wl.Add("CODE");
        Assert.Equal(1, _wl.Excluded.Count(x => x == "code"));
    }

    [Fact] public void Remove_后不再排除()
    {
        _wl.Add("code"); _wl.Remove("code");
        Assert.False(_wl.IsExcluded("code"));
    }

    [Fact] public void 系统关键进程_永远视为受保护()
    {
        Assert.True(_wl.IsSystemCritical("system"));
        Assert.True(_wl.IsSystemCritical("csrss"));
        Assert.True(_wl.IsSystemCritical("explorer"));
        Assert.False(_wl.IsSystemCritical("chrome"));
    }

    [Fact] public void Add_后立即持久化到设置()
    {
        _wl.Add("notepad");
        Assert.Contains("notepad", _settings.Current.ExcludedProcesses);
    }

    [Fact] public void Import_每行一个进程名()
    {
        var f = Path.Combine(_dir, "wl.txt");
        File.WriteAllLines(f, new[] { "foo.exe", "bar", "", "  " });
        _wl.Import(f);
        Assert.True(_wl.IsExcluded("foo"));
        Assert.True(_wl.IsExcluded("bar"));
        Assert.Equal(2, _wl.Excluded.Count);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/AiMemoryManager.Tests --filter WhitelistServiceTests` → 编译失败

- [ ] **Step 3: 实现**

```csharp
namespace AiMemoryManager.Services;

public class WhitelistService
{
    private static readonly HashSet<string> SystemCritical = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "svchost", "dwm", "explorer", "sihost", "taskhostw", "ctfmon",
        "securityhealthservice", "msmpeng", "memory compression", "system idle process"
    };

    private readonly SettingsService _settings;
    public WhitelistService(SettingsService settings) => _settings = settings;

    public IReadOnlyCollection<string> Excluded => _settings.Current.ExcludedProcesses;

    public bool IsExcluded(string processName) =>
        Excluded.Contains(NormalizeName(processName));

    public bool IsSystemCritical(string processName) =>
        SystemCritical.Contains(NormalizeName(processName));

    public void Add(string processName)
    {
        var n = NormalizeName(processName);
        if (n.Length == 0 || Excluded.Contains(n)) return;
        _settings.Current.ExcludedProcesses.Add(n);
        _settings.Save();
    }

    public void Remove(string processName)
    {
        _settings.Current.ExcludedProcesses.Remove(NormalizeName(processName));
        _settings.Save();
    }

    public void Import(string filePath)
    {
        foreach (var line in File.ReadAllLines(filePath))
        {
            var n = NormalizeName(line);
            if (n.Length > 0 && !Excluded.Contains(n))
                _settings.Current.ExcludedProcesses.Add(n);
        }
        _settings.Save();
    }

    public void Export(string filePath) =>
        File.WriteAllLines(filePath, Excluded.Select(n => n + ".exe"));

    private static string NormalizeName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return n.EndsWith(".exe") ? n[..^4] : n;
    }
}
```

- [ ] **Step 4: 运行确认通过** → 6 passed

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "白名单服务:排除列表增删/持久化/导入导出、系统关键进程硬名单(TDD)"
```

---

### Task 5: ForegroundGuard 前台保护

**Files:**
- Create: `src/AiMemoryManager/Services/ForegroundGuard.cs`
- Test: 追加 `tests/AiMemoryManager.Tests/ForegroundGuardTests.cs`

**Interfaces:**
- Consumes: `INativeMemoryApi`
- Produces:
```csharp
public class ForegroundGuard
{
    public ForegroundGuard(INativeMemoryApi native, Func<int> selfPid);
    public bool IsProtected(int pid);        // 前台进程或本进程 → true
    public bool ShouldSuppressAutoClean();   // 设置开启且全屏应用活跃 → true
    public bool IsFullscreenSettingEnabled { get; set; } // 由 SettingsService.OnlyWhenNotFullscreen 驱动
}
```

- [ ] **Step 1: 写失败测试**

```csharp
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class ForegroundGuardTests
{
    [Fact] public void 前台进程受保护()
    {
        var fake = new FakeNativeMemoryApi { ForegroundPid = 1234 };
        var g = new ForegroundGuard(fake, () => 999);
        Assert.True(g.IsProtected(1234));
        Assert.False(g.IsProtected(4321));
    }

    [Fact] public void 本进程始终受保护()
    {
        var fake = new FakeNativeMemoryApi();
        var g = new ForegroundGuard(fake, () => 999);
        Assert.True(g.IsProtected(999));
    }

    [Fact] public void 全屏时且设置开启_抑制自动清理()
    {
        var fake = new FakeNativeMemoryApi { FullscreenActive = true };
        var g = new ForegroundGuard(fake, () => 1) { IsFullscreenSettingEnabled = true };
        Assert.True(g.ShouldSuppressAutoClean());
        g.IsFullscreenSettingEnabled = false;
        Assert.False(g.ShouldSuppressAutoClean());
    }
}
```

- [ ] **Step 2: 确认失败** → 编译失败

- [ ] **Step 3: 实现**

```csharp
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class ForegroundGuard
{
    private readonly INativeMemoryApi _native;
    private readonly Func<int> _selfPid;
    public bool IsFullscreenSettingEnabled { get; set; } = true;

    public ForegroundGuard(INativeMemoryApi native, Func<int> selfPid)
        => (_native, _selfPid) = (native, selfPid);

    public bool IsProtected(int pid) => pid == _selfPid() || pid == _native.GetForegroundPid();

    public bool ShouldSuppressAutoClean() =>
        IsFullscreenSettingEnabled && _native.IsFullscreenAppActive();
}
```

- [ ] **Step 4: 确认通过** → 3 passed

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "前台保护:前台进程与本进程豁免、全屏免打扰判定(TDD)"
```

---

### Task 6: ElevatedHelper 提权程序 + ElevatedL2Service(L2 免重复 UAC 方案)

**Files:**
- Create: `src/AiMemoryManager.ElevatedHelper/Program.cs`, `src/AiMemoryManager/Services/ElevatedL2Service.cs`, `src/AiMemoryManager/Models/CleanResult.cs`(含 `CleanLevel`/`CleanTrigger`)
- Test: `tests/AiMemoryManager.Tests/CleanServiceTests.cs`(ElevatedL2Service 抽接口 `IL2Executor` 以便测试)

**Interfaces:**
- Produces:
```csharp
public interface IL2Executor
{
    Task<long> PurgeStandbyListAsync(CancellationToken ct); // 返回估算释放字节数
    bool IsHelperTaskRegistered { get; }
    void RegisterHelperTask();                               // 触发一次性 UAC
}
public class ElevatedL2Service : IL2Executor { ... }
```

**方案说明(L2 免重复 UAC):** 首次使用 L2 时,主程序以 `runas` 动词启动 `AiMemoryManager.ElevatedHelper.exe --install`(弹一次 UAC),Helper 自注册一个"最高权限、仅手动触发"的计划任务;之后每次 L2 由主程序 `schtasks /run /tn <name>` 静默触发,Helper 清理 Standby List 并把结果 JSON 写入 `%PROGRAMDATA%\AiMemoryManager\l2-result.json`,主程序轮询读取。

- [ ] **Step 1: 写 Helper 程序**

`src/AiMemoryManager.ElevatedHelper/Program.cs`:
```csharp
// 用法:
//   AiMemoryManager.ElevatedHelper.exe --install
//   AiMemoryManager.ElevatedHelper.exe --purge-standby --result <结果文件路径>
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

class Program
{
    const string TaskName = "AiMemoryManager.L2Helper";

    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int InfoClass, ref int Info, int Length);
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int InfoClass, ref SYSTEM_PERFORMANCE_INFORMATION Info, int Length, out int ReturnLength);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool LookupPrivilegeValue(string? sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES tp, int len, IntPtr prev, IntPtr retLen);

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attr; }
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_PERFORMANCE_INFORMATION
    {
        public long IdleProcessTime; public long IoReadTransferCount; public long IoWriteTransferCount; public long IoOtherTransferCount;
        public int IoReadOperationCount; public int IoWriteOperationCount; public int IoOtherOperationCount;
        public int AvailablePages; public int CommittedPages; public int CommitLimit; public int PeakCommitment;
        public int PageFaultCount; public int CopyOnWriteCount; public int TransitionCount; public int CacheTransitionCount;
        public int DemandZeroCount; public int PageReadCount; public int PageReadIoCount; public int CacheReadCount; public int CacheIoCount;
        public int DirtyPagesWriteCount; public int DirtyWriteIoCount; public int MappedPagesWriteCount; public int MappedWriteIoCount;
        public int PagedPoolPages; public int NonPagedPoolPages; public int PagedPoolAllocs; public int PagedPoolFrees;
        public int NonPagedPoolAllocs; public int NonPagedPoolFrees; public int FreeSystemPtes;
        public int ResidentSystemCodePage; public int TotalSystemDriverPages; public int TotalSystemCodePages;
        public int NonPagedPoolLookasideHits; public int PagedPoolLookasideHits; public int AvailablePagedPoolPages;
        public int ResidentSystemCachePage; public int ResidentPagedPoolPage; public int ResidentSystemDriverPage;
        public int CcFastReadNoWait; public int CcFastReadWait; public int CcFastReadResourceMiss; public int CcFastReadNotPossible;
        public int CcFastMdlReadNoWait; public int CcFastMdlReadWait; public int CcFastMdlReadResourceMiss; public int CcFastMdlReadNotPossible;
        public int CcMapDataNoWait; public int CcMapDataWait; public int CcMapDataNoWaitMiss; public int CcMapDataWaitMiss;
        public int CcPinMappedDataCount; public int CcPinReadNoWait; public int CcPinReadWait; public int CcPinReadNoWaitMiss; public int CcPinReadWaitMiss;
        public int CcCopyReadNoWait; public int CcCopyReadWait; public int CcCopyReadNoWaitMiss; public int CcCopyReadWaitMiss;
        public int CcMdlReadNoWait2; public int CcMdlReadWait2; public int CcMdlReadNoWaitMiss2; public int CcMdlReadWaitMiss2;
        public int LookasideHits; public int LookasideMisses; public int Reserved18; public int Reserved19;
        // 后续字段省略 — AvailablePages 位于偏移 48(前 12 个 8 字节 = 96 位前 3 个 long + 3 个 int 后),见 Step 4 校验
    }

    static int Main(string[] args)
    {
        if (args.Contains("--install")) return Install();
        if (args.Contains("--purge-standby"))
        {
            var idx = Array.IndexOf(args, "--result");
            var resultPath = idx >= 0 ? args[idx + 1] : null;
            return Purge(resultPath);
        }
        Console.WriteLine("用法: --install | --purge-standby --result <path>");
        return 2;
    }

    static int Install()
    {
        // 由已提权进程注册"最高权限、手动触发"计划任务
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo("schtasks",
            $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --purge-standby --result \\\"%PROGRAMDATA%\\\\AiMemoryManager\\\\l2-result.json\\\"\" /sc once /st 00:00 /rl HIGHEST /f")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        var p = Process.Start(psi)!;
        p.WaitForExit();
        Console.WriteLine(p.ExitCode == 0 ? "计划任务注册成功" : "注册失败:" + p.StandardOutput.ReadToEnd());
        return p.ExitCode;
    }

    static int Purge(string? resultPath)
    {
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        long before = AvailablePages();
        int command = 4; // MemoryPurgeStandbyList
        int status = NtSetSystemInformation(0x50 /*SystemMemoryListInformation*/, ref command, sizeof(int));
        long after = AvailablePages();
        long freedPages = Math.Max(0, after - before);
        if (resultPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                status,
                freedBytes = freedPages * Environment.SystemPageSize,
                time = DateTimeOffset.Now
            }));
        }
        return status;
    }

    static long AvailablePages()
    {
        var spi = new SYSTEM_PERFORMANCE_INFORMATION();
        NtQuerySystemInformation(2 /*SystemPerformanceInformation*/, ref spi,
            Marshal.SizeOf<SYSTEM_PERFORMANCE_INFORMATION>(), out _);
        return spi.AvailablePages;
    }

    static void EnablePrivilege(string name)
    {
        OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var token);
        LookupPrivilegeValue(null, name, out long luid);
        var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = 0x00000002 /*SE_PRIVILEGE_ENABLED*/ };
        AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
```

> ⚠️ **Step 4 校验点**:`SYSTEM_PERFORMANCE_INFORMATION` 的 `AvailablePages` 字段偏移必须为 40(3 个 long=24 + 4 个 int=16)。上面的结构体若偏移不对,用 `Marshal.OffsetOf` 断言校验;更稳妥的做法是定义精确布局:前 3 个 `long`(IdleProcessTime、IoReadTransferCount、IoWriteTransferCount)后接 4 个 int 才是 AvailablePages?——**以运行结果为准**:Step 4 中用下面的断言程序验证 `OffsetOf("AvailablePages") == 40`,不符则按微软文档(ntdoc.m417oz/deepwiki 均可查)修正字段顺序。

- [ ] **Step 2: 手工验证 Helper(需要管理员 PowerShell)**

```powershell
# 管理员终端:
cd src/AiMemoryManager.ElevatedHelper
dotnet run -- --purge-standby --result "$env:TEMP\l2.json"
cat "$env:TEMP\l2.json"
```
Expected: 输出 JSON,`status=0`,`freedBytes > 0`(可用 RAMMap 对比 Standby 减少)。

- [ ] **Step 3: 实现 ElevatedL2Service**

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace AiMemoryManager.Services;

public interface IL2Executor
{
    bool IsHelperTaskRegistered { get; }
    void RegisterHelperTask();
    Task<long> PurgeStandbyListAsync(CancellationToken ct);
}

public class ElevatedL2Service : IL2Executor
{
    public const string TaskName = "AiMemoryManager.L2Helper";
    private readonly string _helperPath;
    private readonly string _resultPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "AiMemoryManager", "l2-result.json");

    public ElevatedL2Service(string helperPath) => _helperPath = helperPath;

    public bool IsHelperTaskRegistered
    {
        get
        {
            var p = Process.Start(new ProcessStartInfo("schtasks", $"/query /tn \"{TaskName}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
    }

    public void RegisterHelperTask()
    {
        // 唯一一次 UAC:以 runas 启动 Helper 自注册
        var p = Process.Start(new ProcessStartInfo(_helperPath, "--install")
        { UseShellExecute = true, Verb = "runas" })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("计划任务注册失败,exit=" + p.ExitCode);
    }

    public async Task<long> PurgeStandbyListAsync(CancellationToken ct)
    {
        if (!IsHelperTaskRegistered) RegisterHelperTask();
        try { File.Delete(_resultPath); } catch { }
        var run = Process.Start(new ProcessStartInfo("schtasks", $"/run /tn \"{TaskName}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        run.WaitForExit();

        // 轮询结果文件,最多 15 秒
        for (int i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(_resultPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_resultPath));
                    return doc.RootElement.GetProperty("freedBytes").GetInt64();
                }
                catch (IOException) { /* 文件尚在写入,继续等 */ }
            }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("L2 清理结果等待超时");
    }
}
```

- [ ] **Step 4: 集成验证(手工)**

主程序调试目录下放 Helper 编译产物(在 `AiMemoryManager.csproj` 加项目引用 + 后期复制,或 Task 13 的集成脚本处理)。在管理员/普通账户下各跑一次 `PurgeStandbyListAsync`:首次弹一次 UAC,后续静默。记录结果。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "L2方案:提权Helper(Standby清理)+ 计划任务免重复UAC调度"
```

---

### Task 7: CleanService L1(TDD)

**Files:**
- Create: `src/AiMemoryManager/Services/CleanService.cs`, `src/AiMemoryManager/Models/CleanResult.cs`
- Test: `tests/AiMemoryManager.Tests/CleanServiceTests.cs`

**Interfaces:**
- Consumes: `INativeMemoryApi`, `WhitelistService`, `ForegroundGuard`, `IL2Executor`
- Produces: 头部 `CleanService` 签名

- [ ] **Step 1: 写失败测试**

```csharp
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class FakeL2 : IL2Executor
{
    public bool IsHelperTaskRegistered => true;
    public void RegisterHelperTask() { }
    public long Freed { get; set; } = 500L << 20;
    public int Calls { get; private set; }
    public Task<long> PurgeStandbyListAsync(CancellationToken ct) { Calls++; return Task.FromResult(Freed); }
}

public class CleanServiceTests
{
    private static (CleanService svc, FakeNativeMemoryApi native, FakeL2 l2) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "s.json"));
        settings.Load();
        var native = new FakeNativeMemoryApi
        {
            Processes =
            {
                new(1, "chrome", null, 800L << 20, true),
                new(2, "code", null, 500L << 20, true),
                new(3, "csrss", null, 10L << 20, false),
            },
            ForegroundPid = 1
        };
        var wl = new WhitelistService(settings);
        wl.Add("code");
        var guard = new ForegroundGuard(native, () => 999);
        var l2 = new FakeL2();
        return (new CleanService(native, wl, l2, guard), native, l2);
    }

    [Fact] public async Task L1_跳过白名单_系统关键_前台进程()
    {
        var (svc, native, _) = Create();
        var r = await svc.RunL1Async(CleanTrigger.Manual);
        Assert.DoesNotContain(2, native.EmptiedPids);  // 白名单
        Assert.DoesNotContain(3, native.EmptiedPids);  // 系统关键
        Assert.DoesNotContain(1, native.EmptiedPids);  // 前台
        Assert.Equal(0, r.ProcessCount);
    }

    [Fact] public async Task L1_普通进程被清理并统计()
    {
        var (svc, native, _) = Create();
        native.ForegroundPid = -1;
        native.Processes.Add(new(4, "notepad", null, 100L << 20, true));
        var r = await svc.RunL1Async(CleanTrigger.Manual);
        Assert.Single(native.EmptiedPids);
        Assert.Equal(100L << 20, r.FreedBytes);
        Assert.Equal(1, r.ProcessCount);
        Assert.Equal(CleanTrigger.Manual, r.Trigger);
    }

    [Fact] public async Task L2_调用提权执行器并回报释放量()
    {
        var (svc, _, l2) = Create();
        var r = await svc.RunL2Async(CleanTrigger.Manual);
        Assert.Equal(1, l2.Calls);
        Assert.Equal(500L << 20, r.FreedBytes);
    }

    [Fact] public async Task 清理完成触发事件()
    {
        var (svc, native, _) = Create();
        native.ForegroundPid = -1;
        CleanResult? got = null;
        svc.CleanCompleted += (_, r) => got = r;
        await svc.RunL1Async(CleanTrigger.Tray);
        Assert.NotNull(got);
        Assert.Equal(CleanTrigger.Tray, got!.Trigger);
    }
}
```

- [ ] **Step 2: 确认失败** → 编译失败

- [ ] **Step 3: 实现**

`Models/CleanResult.cs`:
```csharp
namespace AiMemoryManager.Models;
public enum CleanLevel { L1, L2 }
public enum CleanTrigger { Manual, RuleThreshold, RuleTimer, Tray }
public record CleanResult(DateTimeOffset Time, CleanLevel Level, long FreedBytes, int ProcessCount, CleanTrigger Trigger);
```

`Services/CleanService.cs`:
```csharp
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class CleanService
{
    private readonly INativeMemoryApi _native;
    private readonly WhitelistService _whitelist;
    private readonly IL2Executor _l2;
    private readonly ForegroundGuard _guard;

    public event EventHandler<CleanResult>? CleanCompleted;

    public CleanService(INativeMemoryApi native, WhitelistService whitelist, IL2Executor l2, ForegroundGuard guard)
        => (_native, _whitelist, _l2, _guard) = (native, whitelist, l2, guard);

    public Task<CleanResult> RunL1Async(CleanTrigger trigger, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var targets = _native.GetProcessSnapshots()
                .Where(p => p.WorkingSetBytes > 20L << 20)           // 跳过极小进程
                .Where(p => !_whitelist.IsExcluded(p.Name))
                .Where(p => !_whitelist.IsSystemCritical(p.Name))
                .Where(p => !_guard.IsProtected(p.Pid))
                .Select(p => p.Pid)
                .ToList();
            long freed = _native.EmptyWorkingSets(targets);
            var result = new CleanResult(DateTimeOffset.Now, CleanLevel.L1, freed, targets.Count, trigger);
            CleanCompleted?.Invoke(this, result);
            return result;
        }, ct);

    public async Task<CleanResult> RunL2Async(CleanTrigger trigger, CancellationToken ct = default)
    {
        long freed = await _l2.PurgeStandbyListAsync(ct);
        var result = new CleanResult(DateTimeOffset.Now, CleanLevel.L2, freed, 0, trigger);
        CleanCompleted?.Invoke(this, result);
        return result;
    }
}
```

注意:`CleanService` 构造参数第 3 位是 `IL2Executor`(接口),测试传 `FakeL2`,生产传 `ElevatedL2Service`。

- [ ] **Step 4: 确认通过** → 4 passed

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "清理服务:L1 工作集压缩(白名单/系统/前台三重豁免)、L2 对接(TDD)"
```

---

### Task 8: RuleEngine 规则引擎(TDD)

**Files:**
- Create: `src/AiMemoryManager/Services/RuleEngine.cs`
- Test: `tests/AiMemoryManager.Tests/RuleEngineTests.cs`

**Interfaces:**
- Consumes: `SettingsService`, `INativeMemoryApi`, `ForegroundGuard`
- Produces: 头部 `RuleEngine`/`CleanRequest` 签名;`Tick()` 每 10 秒由 UI 定时器调用一次(常量 `TickIntervalSeconds = 10`)

- [ ] **Step 1: 写失败测试**

```csharp
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.Tests.Fakes;

namespace AiMemoryManager.Tests;

public class RuleEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly FakeNativeMemoryApi _native = new();
    private readonly ForegroundGuard _guard;
    private DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    public RuleEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsService(Path.Combine(_dir, "s.json"));
        _settings.Load();
        _guard = new ForegroundGuard(_native, () => 1);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private RuleEngine Create(out List<CleanRequest> fired)
    {
        var list = new List<CleanRequest>();
        var e = new RuleEngine(_settings, _native, _guard, () => _now);
        e.CleanRequested += (_, r) => list.Add(r);
        fired = list;
        return e;
    }

    private void SetUsage(double percent) =>
        _native.Memory = new SystemMemoryInfo(1000, (long)(1000 * (1 - percent / 100)));

    [Fact] public void 阈值规则_未持续超阈不触发()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 30;         // 需要 3 次连续 tick
        var e = Create(out var fired);
        SetUsage(90);
        e.Tick(); e.Tick();
        Assert.Empty(fired);
    }

    [Fact] public void 阈值规则_持续超阈后触发且带冷却()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 30;
        var e = Create(out var fired);
        SetUsage(90);
        e.Tick(); e.Tick(); e.Tick();
        Assert.Single(fired);
        Assert.Equal(CleanTrigger.RuleThreshold, fired[0].Trigger);
        e.Tick(); e.Tick(); e.Tick();                  // 冷却 5 分钟内不重复
        Assert.Single(fired);
        _now += TimeSpan.FromMinutes(6);               // 过冷却后再触发
        e.Tick(); e.Tick(); e.Tick();
        Assert.Equal(2, fired.Count);
    }

    [Fact] public void 占用回落后计数清零()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 30;
        var e = Create(out var fired);
        SetUsage(90); e.Tick(); e.Tick();
        SetUsage(50); e.Tick();
        SetUsage(90); e.Tick(); e.Tick();
        Assert.Empty(fired);
    }

    [Fact] public void 全屏时阈值规则被抑制()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 10;
        _settings.Current.OnlyWhenNotFullscreen = true;
        _guard.IsFullscreenSettingEnabled = true;
        _native.FullscreenActive = true;
        var e = Create(out var fired);
        SetUsage(95);
        e.Tick(); e.Tick(); e.Tick();
        Assert.Empty(fired);
    }

    [Fact] public void 定时规则_到点触发()
    {
        _settings.Current.TimerRuleEnabled = true;
        _settings.Current.TimerIntervalMinutes = 60;
        var e = Create(out var fired);
        SetUsage(10);
        e.Tick();                                       // 首次不触发
        _now += TimeSpan.FromMinutes(61);
        e.Tick();
        Assert.Single(fired);
        Assert.Equal(CleanTrigger.RuleTimer, fired[0].Trigger);
    }

    [Fact] public void 触发级别跟随AutoCleanIncludeL2设置()
    {
        _settings.Current.ThresholdRuleEnabled = true;
        _settings.Current.SustainSeconds = 10;
        _settings.Current.AutoCleanIncludeL2 = true;
        var e = Create(out var fired);
        SetUsage(95);
        e.Tick();
        Assert.Equal(CleanLevel.L2, fired[0].Level);
    }
}
```

- [ ] **Step 2: 确认失败** → 编译失败

- [ ] **Step 3: 实现**

```csharp
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public record CleanRequest(CleanLevel Level, CleanTrigger Trigger);

public class RuleEngine
{
    public const int TickIntervalSeconds = 10;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private readonly SettingsService _settings;
    private readonly INativeMemoryApi _native;
    private readonly ForegroundGuard _guard;
    private readonly Func<DateTimeOffset> _clock;

    private int _overCount;
    private DateTimeOffset _lastFire = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTimerFire;

    public event EventHandler<CleanRequest>? CleanRequested;

    public RuleEngine(SettingsService settings, INativeMemoryApi native, ForegroundGuard guard, Func<DateTimeOffset> clock)
    {
        (_settings, _native, _guard, _clock) = (settings, native, guard, clock);
        _lastTimerFire = clock();
    }

    public void Tick()
    {
        var s = _settings.Current;
        _guard.IsFullscreenSettingEnabled = s.OnlyWhenNotFullscreen;
        var now = _clock();
        var level = s.AutoCleanIncludeL2 ? CleanLevel.L2 : CleanLevel.L1;

        if (s.ThresholdRuleEnabled && !_guard.ShouldSuppressAutoClean())
        {
            bool over = _native.GetSystemMemory().UsedPercent >= s.ThresholdPercent;
            _overCount = over ? _overCount + 1 : 0;
            int need = Math.Max(1, s.SustainSeconds / TickIntervalSeconds);
            if (_overCount >= need && now - _lastFire >= Cooldown)
            {
                _lastFire = now;
                _overCount = 0;
                CleanRequested?.Invoke(this, new CleanRequest(level, CleanTrigger.RuleThreshold));
            }
        }
        else _overCount = 0;

        if (s.TimerRuleEnabled && !_guard.ShouldSuppressAutoClean()
            && now - _lastTimerFire >= TimeSpan.FromMinutes(s.TimerIntervalMinutes))
        {
            _lastTimerFire = now;
            CleanRequested?.Invoke(this, new CleanRequest(level, CleanTrigger.RuleTimer));
        }
    }
}
```

- [ ] **Step 4: 确认通过** → 6 passed

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "规则引擎:阈值持续判定+冷却、定时规则、全屏抑制、级别联动(TDD)"
```

---

### Task 9: LocalizationService 中英文(TDD)

**Files:**
- Create: `src/AiMemoryManager/Services/LocalizationService.cs`, `src/AiMemoryManager/Assets/i18n/en.json`, `src/AiMemoryManager/Assets/i18n/zh-CN.json`
- Test: `tests/AiMemoryManager.Tests/LocalizationServiceTests.cs`

**Interfaces:**
- Produces: 头部 `LocalizationService` 签名。XAML 绑定方式:`{Binding [Key], Source={x:Static services:Locator.L10n}}`(Task 12 定义 `Locator`)。

- [ ] **Step 1: 写失败测试**

```csharp
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class LocalizationServiceTests
{
    private static string I18nDir => Path.Combine(AppContext.BaseDirectory, "i18n-test");

    public LocalizationServiceTests()
    {
        Directory.CreateDirectory(I18nDir);
        File.WriteAllText(Path.Combine(I18nDir, "en.json"), """{ "App.Title": "AI Memory Manager", "Action.Clean": "Clean Now" }""");
        File.WriteAllText(Path.Combine(I18nDir, "zh-CN.json"), """{ "App.Title": "AI 内存管家", "Action.Clean": "一键清理" }""");
    }

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
        Assert.Equal("Clean Now", l["Action.Clean"]);
        l.CurrentLanguage = "zh-CN";
        Assert.Equal("一键清理", l["Action.Clean"]);
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
```

测试前把 `i18n-test` 目录加入测试项目输出(csproj 中用 `None Update` 复制,或测试中直接指向源码相对路径)。最简单:测试类里指向 `src/AiMemoryManager/Assets/i18n` 的相对路径……为稳定起见,**把两份真实 JSON 先写好(Step 3),测试直接读源文件路径**:

```csharp
private static string I18nDir => Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AiMemoryManager", "Assets", "i18n"));
```

(若路径层级在 CI 中不稳定,退而在测试项目 csproj 中复制:
`<None Include="..\..\src\AiMemoryManager\Assets\i18n\*.json" CopyToOutputDirectory="PreserveNewest" Link="i18n\%(Filename)%(Extension)" />`,然后 `I18nDir = Path.Combine(AppContext.BaseDirectory, "i18n")`。)

- [ ] **Step 2: 确认失败** → 编译失败

- [ ] **Step 3: 实现 + 词典**

```csharp
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace AiMemoryManager.Services;

public class LocalizationService : INotifyPropertyChanged
{
    private readonly string _dir;
    private Dictionary<string, string> _dict = new();
    private string _lang = "zh-CN";

    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService(string i18nDir)
    {
        _dir = i18nDir;
        LoadIntoDict(_lang);
    }

    public string CurrentLanguage
    {
        get => _lang;
        set
        {
            if (_lang == value) return;
            _lang = value;
            LoadIntoDict(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public string this[string key] =>
        _dict.TryGetValue(key, out var v) ? v : key;

    public void SetAuto() =>
        CurrentLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en";

    private void LoadIntoDict(string lang)
    {
        var path = Path.Combine(_dir, lang + ".json");
        try { _dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(); }
        catch { _dict = new(); }
    }
}
```

`Assets/i18n/zh-CN.json`(en.json 同 key、英文文案,key 全集即 M1 全部界面文案):
```json
{
  "App.Title": "AI 内存管家",
  "Nav.Dashboard": "仪表盘", "Nav.Processes": "进程", "Nav.Rules": "规则",
  "Nav.Whitelist": "白名单", "Nav.Settings": "设置",
  "Dashboard.UsedMemory": "内存占用", "Dashboard.Total": "总计",
  "Dashboard.Available": "可用", "Dashboard.CleanL1": "一键清理",
  "Dashboard.CleanL2": "深度清理(需管理员)", "Dashboard.LastClean": "上次清理释放 {0} MB",
  "Processes.Col.Name": "进程", "Processes.Col.Memory": "内存", "Processes.Col.Path": "路径",
  "Processes.AddWhitelist": "加入白名单",
  "Rules.Threshold": "内存超过", "Rules.ThresholdSuffix": "% 持续",
  "Rules.SustainSuffix": "秒时自动清理", "Rules.Timer": "每隔",
  "Rules.TimerSuffix": "分钟自动清理", "Rules.IncludeL2": "自动清理包含深度清理",
  "Rules.NoFullscreen": "游戏/全屏时暂停自动清理",
  "Whitelist.Empty": "暂无白名单,可在进程页右键添加",
  "Whitelist.Import": "导入", "Whitelist.Export": "导出", "Whitelist.Remove": "移除",
  "Settings.Language": "语言", "Settings.LangAuto": "跟随系统",
  "Settings.Animations": "界面动效", "Settings.About": "关于",
  "Tray.Clean": "一键清理", "Tray.Open": "打开主界面", "Tray.Exit": "退出",
  "Clean.Done": "清理完成,释放 {0} MB"
}
```

csproj 中加入:
```xml
<ItemGroup>
  <None Update="Assets\i18n\*.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
</ItemGroup>
```

- [ ] **Step 4: 确认通过** → 3 passed

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "多语言:JSON 词典 + 运行时即时切换服务(TDD),中英文案全集"
```

---

### Task 10: MemoryMonitorService 采样 + PercentageRing 控件

**Files:**
- Create: `src/AiMemoryManager/Services/MemoryMonitorService.cs`, `src/AiMemoryManager/Controls/PercentageRing.xaml(.cs)`

**Interfaces:**
- Consumes: `INativeMemoryApi`
- Produces:
```csharp
public class MemoryMonitorService : IDisposable
{
    public MemoryMonitorService(INativeMemoryApi native, int intervalMs = 2000);
    public event EventHandler<SystemMemoryInfo>? Sampled;
    public IReadOnlyList<double> RecentPercents { get; }  // 最近 150 个采样(5 分钟),供曲线
    public void Start();
    public void Dispose();
}
```
`PercentageRing`:依赖属性 `Percent(double)`、`StrokeThickness`、`RingBrush`;圆弧自绘。

- [ ] **Step 1: 实现 MemoryMonitorService**

```csharp
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class MemoryMonitorService : IDisposable
{
    private const int MaxSamples = 150;
    private readonly INativeMemoryApi _native;
    private readonly Timer _timer;
    private readonly List<double> _recent = new();

    public event EventHandler<SystemMemoryInfo>? Sampled;
    public IReadOnlyList<double> RecentPercents => _recent;

    public MemoryMonitorService(INativeMemoryApi native, int intervalMs = 2000)
    {
        _native = native;
        _timer = new Timer(_ => Sample(), null, Timeout.Infinite, intervalMs);
    }

    public void Start() => _timer.Change(0, 2000);

    private void Sample()
    {
        var info = _native.GetSystemMemory();
        lock (_recent)
        {
            _recent.Add(info.UsedPercent);
            if (_recent.Count > MaxSamples) _recent.RemoveAt(0);
        }
        Sampled?.Invoke(this, info);
    }

    public void Dispose() => _timer.Dispose();
}
```

- [ ] **Step 2: 实现 PercentageRing**

`Controls/PercentageRing.xaml`:
```xml
<UserControl x:Class="AiMemoryManager.Controls.PercentageRing"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <Path x:Name="Track" Stroke="{Binding TrackBrush, RelativeSource={RelativeSource AncestorType=UserControl}}"
              StrokeThickness="{Binding StrokeThickness, RelativeSource={RelativeSource AncestorType=UserControl}}"
              Fill="None" Opacity="0.2"/>
        <Path x:Name="Arc" Stroke="{Binding RingBrush, RelativeSource={RelativeSource AncestorType=UserControl}}"
              StrokeThickness="{Binding StrokeThickness, RelativeSource={RelativeSource AncestorType=UserControl}}"
              StrokeStartLineCap="Round" StrokeEndLineCap="Round" Fill="None"/>
    </Grid>
</UserControl>
```

`Controls/PercentageRing.xaml.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiMemoryManager.Controls;

public partial class PercentageRing : UserControl
{
    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(PercentageRing),
            new PropertyMetadata(0.0, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(PercentageRing),
            new PropertyMetadata(10.0, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty RingBrushProperty =
        DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(PercentageRing),
            new PropertyMetadata(Brushes.DodgerBlue, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(PercentageRing),
            new PropertyMetadata(Brushes.Gray, (d, _) => ((PercentageRing)d).Redraw()));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public PercentageRing() { InitializeComponent(); SizeChanged += (_, _) => Redraw(); }

    private void Redraw()
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= StrokeThickness) return;
        double r = (size - StrokeThickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double angle = Math.Clamp(Percent, 0, 100) / 100.0 * 2 * Math.PI - Math.PI / 2; // 从 12 点方向起

        Track.Data = new EllipseGeometry(center, r, r);

        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
        bool large = Percent > 50;
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, large, SweepDirection.Clockwise, true));
        if (Percent >= 99.9) // 整圆退化为椭圆
            Arc.Data = new EllipseGeometry(center, r, r);
        else if (Percent <= 0.01)
            Arc.Data = null;
        else
            Arc.Data = new PathGeometry(new[] { fig });
    }
}
```

- [ ] **Step 3: 构建验证 + Commit**

Run: `dotnet build AiMemoryManager.sln` → 0 error
```bash
git add -A
git commit -m "监控采样服务 + 仪表盘圆环控件(自绘圆弧)"
```

---

### Task 11: 应用外壳(App.xaml DI、单实例、Locator、FluentWindow 导航、Mica、主题跟随)

**Files:**
- Modify: `src/AiMemoryManager/App.xaml(.cs)`, `src/AiMemoryManager/MainWindow.xaml(.cs)`
- Create: `src/AiMemoryManager/Services/Locator.cs`(静态服务定位器,M1 不引入完整 DI 容器)

**Interfaces:**
- Consumes: Task 3-10 全部服务
- Produces: `Locator`(静态属性:`Settings`、`Whitelist`、`L10n`、`Monitor`、`Clean`、`Rules`、`Guard`、`L2`、`Native`),五个页面的导航宿主。

- [ ] **Step 1: 实现 Locator 与 App 启动**

`Services/Locator.cs`:
```csharp
using System.Diagnostics;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public static class Locator
{
    public static INativeMemoryApi Native { get; private set; } = new NativeMemoryApi();
    public static SettingsService Settings { get; private set; } = null!;
    public static WhitelistService Whitelist { get; private set; } = null!;
    public static ForegroundGuard Guard { get; private set; } = null!;
    public static IL2Executor L2 { get; private set; } = null!;
    public static CleanService Clean { get; private set; } = null!;
    public static RuleEngine Rules { get; private set; } = null!;
    public static MemoryMonitorService Monitor { get; private set; } = null!;
    public static LocalizationService L10n { get; private set; } = null!;

    public static void Init()
    {
        Settings = new SettingsService(SettingsService.DefaultPath());
        Settings.Load();
        Whitelist = new WhitelistService(Settings);
        Guard = new ForegroundGuard(Native, () => Environment.ProcessId);
        var helperPath = Path.Combine(AppContext.BaseDirectory, "AiMemoryManager.ElevatedHelper.exe");
        L2 = new ElevatedL2Service(helperPath);
        Clean = new CleanService(Native, Whitelist, L2, Guard);
        Rules = new RuleEngine(Settings, Native, Guard, () => DateTimeOffset.Now);
        Monitor = new MemoryMonitorService(Native);
        L10n = new LocalizationService(Path.Combine(AppContext.BaseDirectory, "Assets", "i18n"));
        if (Settings.Current.Language == "auto") L10n.SetAuto();
        else L10n.CurrentLanguage = Settings.Current.Language;
    }
}
```

`App.xaml.cs`:
```csharp
using System.Windows;
using System.Windows.Threading;
using AiMemoryManager.Services;

namespace AiMemoryManager;

public partial class App : Application
{
    private Mutex? _mutex;
    private DispatcherTimer? _ruleTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "AiMemoryManager.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }              // 单实例

        Locator.Init();
        Locator.Monitor.Start();

        _ruleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RuleEngine.TickIntervalSeconds) };
        _ruleTimer.Tick += (_, _) => Locator.Rules.Tick();
        _ruleTimer.Start();

        Locator.Rules.CleanRequested += async (_, req) =>
        {
            if (req.Level == Models.CleanLevel.L2) await Locator.Clean.RunL2Async(req.Trigger);
            else await Locator.Clean.RunL1Async(req.Trigger);
        };

        base.OnStartup(e);
    }
}
```

- [ ] **Step 2: MainWindow — FluentWindow + Mica + NavigationView**

```xml
<ui:FluentWindow x:Class="AiMemoryManager.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    xmlns:views="clr-namespace:AiMemoryManager.Views"
    Title="AI Memory Manager" Width="960" Height="640" MinWidth="480" MinHeight="480"
    WindowBackdropType="Mica" ExtendsContentIntoTitleBar="True">
    <Grid>
        <ui:NavigationView x:Name="RootNavigation" IsBackButtonVisible="Collapsed">
            <ui:NavigationView.MenuItems>
                <ui:NavigationViewItem Content="仪表盘" Tag="dashboard" Icon="{ui:SymbolIcon Home24}">
                    <ui:NavigationViewItem.MenuItemsSource><x:Null/></ui:NavigationViewItem.MenuItemsSource>
                </ui:NavigationViewItem>
                <ui:NavigationViewItem Content="进程" Tag="processes" Icon="{ui:SymbolIcon AppsList24}"/>
                <ui:NavigationViewItem Content="规则" Tag="rules" Icon="{ui:SymbolIcon Timer24}"/>
                <ui:NavigationViewItem Content="白名单" Tag="whitelist" Icon="{ui:SymbolIcon ShieldCheckmark24}"/>
            </ui:NavigationView.MenuItems>
            <ui:NavigationView.FooterMenuItems>
                <ui:NavigationViewItem Content="设置" Tag="settings" Icon="{ui:SymbolIcon Settings24}"/>
            </ui:NavigationView.FooterMenuItems>
            <Frame x:Name="RootFrame" NavigationUIVisibility="Hidden"/>
        </ui:NavigationView>
    </Grid>
</ui:FluentWindow>
```

`MainWindow.xaml.cs`:
```csharp
using System.Windows;
using AiMemoryManager.Views;

namespace AiMemoryManager;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        RootNavigation.SelectionChanged += (_, _) =>
        {
            var tag = (RootNavigation.SelectedItem?.Tag as string) ?? "dashboard";
            RootFrame.Navigate(tag switch
            {
                "processes" => new ProcessesPage(),
                "rules" => new RulesPage(),
                "whitelist" => new WhitelistPage(),
                "settings" => new SettingsPage(),
                _ => new DashboardPage(),
            });
        };
        RootFrame.Navigate(new DashboardPage());
        Loaded += (_, _) => Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
    }
}
```

> 菜单文字暂为硬编码中文?——**否**:按 FR-9,所有菜单 Content 绑定本地化。用 `{Binding [Nav.Dashboard], Source={x:Static services:Locator+L10n}}`(XAML 静态嵌套类引用语法 `x:Static services:Locator.L10n` 对静态属性可用)。实施时逐页绑定,本步骤先保证外壳编译通过。

- [ ] **Step 3: 运行验证(手工)**

Run: `dotnet run --project src/AiMemoryManager`
Expected: 窗口出现,Mica 背景,导航可切换空页面占位(先建五个空 Page 类)。
再次启动第二个实例 → 立即退出。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "应用外壳:单实例、服务定位器、FluentWindow+Mica、导航与主题跟随系统"
```

---

### Task 12: 仪表盘页(FR-1.1/1.3、FR-2.1/2.2/2.4、FR-10.5/10.6/10.7)

**Files:**
- Create: `src/AiMemoryManager/Views/DashboardPage.xaml(.cs)`, `src/AiMemoryManager/ViewModels/DashboardViewModel.cs`

**Interfaces:**
- Consumes: `Locator.Monitor`(Sampled/RecentPercents)、`Locator.Clean`、`PercentageRing`

- [ ] **Step 1: 实现 ViewModel**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

public partial class DashboardViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _usedText = "-";
    [ObservableProperty] private string _availableText = "-";
    [ObservableProperty] private string _totalText = "-";
    [ObservableProperty] private string _lastCleanText = "";
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private List<double> _recent = new();

    public DashboardViewModel()
    {
        Locator.Monitor.Sampled += OnSampled;
        Locator.Clean.CleanCompleted += OnCleaned;
        Refresh();
    }

    private void OnSampled(object? s, SystemMemoryInfo info) =>
        App.Current.Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        var m = Locator.Monitor;
        var info = Locator.Native.GetSystemMemory();
        Percent = info.UsedPercent;
        TotalText = $"{info.TotalBytes / (1 << 30)} GB";
        AvailableText = $"{info.AvailableBytes / (1 << 30)} GB";
        UsedText = $"{(info.TotalBytes - info.AvailableBytes) / (1 << 30)} GB";
        Recent = m.RecentPercents.ToList();
    }

    private void OnCleaned(object? s, CleanResult r) =>
        App.Current.Dispatcher.Invoke(() =>
            LastCleanText = string.Format(Locator.L10n["Dashboard.LastClean"], r.FreedBytes / (1 << 20)));

    [RelayCommand]
    private async Task CleanL1Async()
    {
        IsCleaning = true;
        try { await Locator.Clean.RunL1Async(CleanTrigger.Manual); Refresh(); }
        finally { IsCleaning = false; }
    }

    [RelayCommand]
    private async Task CleanL2Async()
    {
        IsCleaning = true;
        try { await Locator.Clean.RunL2Async(CleanTrigger.Manual); Refresh(); }
        catch (Exception ex) { LastCleanText = ex.Message; }   // 首版简单展示,M3 换 InfoBar
        finally { IsCleaning = false; }
    }

    public void Dispose() => Locator.Monitor.Sampled -= OnSampled;
}
```

- [ ] **Step 2: 实现页面 XAML(卡片式 + 英雄圆环 + 迷你曲线)**

```xml
<ui:Page x:Class="AiMemoryManager.Views.DashboardPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    xmlns:c="clr-namespace:AiMemoryManager.Controls"
    xmlns:vm="clr-namespace:AiMemoryManager.ViewModels"
    xmlns:services="clr-namespace:AiMemoryManager.Services">
    <Page.DataContext><vm:DashboardViewModel/></Page.DataContext>
    <StackPanel Margin="24">
        <!-- 英雄区:大圆环 + 主操作 -->
        <Grid Margin="0,12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Grid Width="180" Height="180">
                <c:PercentageRing Percent="{Binding Percent}" StrokeThickness="12"
                                  RingBrush="{ui:ThemeResource SystemFillColorAttentionBrush}"
                                  TrackBrush="{ui:ThemeResource TextFillColorSecondaryBrush}"/>
                <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
                    <TextBlock Text="{Binding Percent, StringFormat=F0}" FontSize="40"
                               FontWeight="SemiBold" HorizontalAlignment="Center"/>
                    <TextBlock Text="{Binding [Dashboard.UsedMemory], Source={x:Static services:Locator+L10n}}"
                               Foreground="{ui:ThemeResource TextFillColorSecondaryBrush}" HorizontalAlignment="Center"/>
                </StackPanel>
            </Grid>
            <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="32,0,0,0">
                <ui:Button Content="{Binding [Dashboard.CleanL1], Source={x:Static services:Locator+L10n}}"
                           Appearance="Primary" Command="{Binding CleanL1Command}"
                           IsEnabled="{Binding IsCleaning, Converter={ui:BoolToInverseBoolConverter}}"
                           Width="200" Height="40"/>
                <ui:Button Content="{Binding [Dashboard.CleanL2], Source={x:Static services:Locator+L10n}}"
                           Command="{Binding CleanL2Command}" Margin="0,12,0,0" Width="200"/>
                <TextBlock Text="{Binding LastCleanText}" Margin="0,12,0,0"
                           Foreground="{ui:ThemeResource TextFillColorSecondaryBrush}"/>
            </StackPanel>
        </Grid>
        <!-- 信息卡片行 -->
        <UniformGrid Columns="3" Margin="0,12">
            <ui:Card Margin="0,0,8,0" Padding="16">
                <StackPanel>
                    <TextBlock Text="{Binding [Dashboard.Total], Source={x:Static services:Locator+L10n}}"
                               Foreground="{ui:ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock Text="{Binding TotalText}" FontSize="24" FontWeight="SemiBold"/>
                </StackPanel>
            </ui:Card>
            <ui:Card Margin="4,0" Padding="16">
                <StackPanel>
                    <TextBlock Text="{Binding [Dashboard.Available], Source={x:Static services:Locator+L10n}}"
                               Foreground="{ui:ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock Text="{Binding AvailableText}" FontSize="24" FontWeight="SemiBold"/>
                </StackPanel>
            </ui:Card>
            <ui:Card Margin="8,0,0,0" Padding="16">
                <StackPanel>
                    <TextBlock Text="已用" Foreground="{ui:ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock Text="{Binding UsedText}" FontSize="24" FontWeight="SemiBold"/>
                </StackPanel>
            </ui:Card>
        </UniformGrid>
        <!-- 迷你曲线(最近 5 分钟) -->
        <ui:Card Padding="16" Margin="0,4">
            <Polyline x:Name="MiniChart" Height="60" Stretch="Fill"
                      Stroke="{ui:ThemeResource SystemFillColorAttentionBrush}" StrokeThickness="2"/>
        </ui:Card>
    </StackPanel>
</ui:Page>
```

`DashboardPage.xaml.cs`:订阅 VM 的 `Recent` 变化,把 150 个百分比映射为 `PointCollection` 赋给 `MiniChart.Points`(X=索引,Y=100-值)。

> 说明:`BoolToInverseBoolConverter` 若 WPF-UI 当前版本无此转换器,改用自定义一行转换器或去掉 IsEnabled 绑定。"已用"等零星漏网文案一律补进 i18n 词典(实施时同步补 key,如 `Dashboard.Used`)。

- [ ] **Step 3: 运行验证(手工)**

Expected: 圆环实时跳动、卡片数字正确、一键清理后释放量文案出现、曲线滚动。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "仪表盘:英雄圆环+一键/深度清理+信息卡片+5分钟曲线"
```

---

### Task 13: 进程页 + 白名单页(FR-1.2、FR-7.1/7.3/7.4)

**Files:**
- Create: `src/AiMemoryManager/Views/ProcessesPage.xaml(.cs)`, `src/AiMemoryManager/ViewModels/ProcessesViewModel.cs`, `src/AiMemoryManager/Views/WhitelistPage.xaml(.cs)`, `src/AiMemoryManager/ViewModels/WhitelistViewModel.cs`

- [ ] **Step 1: ProcessesViewModel**

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

public partial class ProcessItemViewModel : ObservableObject
{
    public required ProcessSnapshot Snapshot { get; init; }
    public string Name => Snapshot.Name;
    public string MemoryText => $"{Snapshot.WorkingSetBytes / (1 << 20)} MB";
    public string? Path => Snapshot.Path;
    [ObservableProperty] private bool _isExcluded;
    [ObservableProperty] private bool _isCritical;
}

public partial class ProcessesViewModel : ObservableObject
{
    public ObservableCollection<ProcessItemViewModel> Items { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        Items.Clear();
        foreach (var p in Locator.Native.GetProcessSnapshots()
                     .Where(p => p.WorkingSetBytes > 10L << 20)
                     .OrderByDescending(p => p.WorkingSetBytes))
        {
            Items.Add(new ProcessItemViewModel
            {
                Snapshot = p,
                IsExcluded = Locator.Whitelist.IsExcluded(p.Name),
                IsCritical = Locator.Whitelist.IsSystemCritical(p.Name)
            });
        }
    }

    [RelayCommand]
    private void AddToWhitelist(ProcessItemViewModel? item)
    {
        if (item == null || item.IsCritical) return;
        Locator.Whitelist.Add(item.Name);
        item.IsExcluded = true;
    }
}
```

- [ ] **Step 2: 进程页 XAML(DataGrid + 右键菜单)**

```xml
<ui:Page x:Class="AiMemoryManager.Views.ProcessesPage" ...>
    <Page.DataContext><vm:ProcessesViewModel/></Page.DataContext>
    <Grid Margin="24">
        <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition/></Grid.RowDefinitions>
        <ui:Button Content="刷新" Command="{Binding RefreshCommand}" HorizontalAlignment="Left"/>
        <DataGrid Grid.Row="1" ItemsSource="{Binding Items}" AutoGenerateColumns="False"
                  IsReadOnly="True" Margin="0,12,0,0">
            <DataGrid.ContextMenu>
                <ContextMenu>
                    <MenuItem Header="{Binding [Processes.AddWhitelist], Source={x:Static services:Locator+L10n}}"
                              Command="{Binding DataContext.AddToWhitelistCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}"
                              CommandParameter="{Binding}"/>
                </ContextMenu>
            </DataGrid.ContextMenu>
            <DataGrid.Columns>
                <DataGridTextColumn Header="进程" Binding="{Binding Name}" Width="2*"/>
                <DataGridTextColumn Header="内存" Binding="{Binding MemoryText}" Width="*"/>
                <DataGridTextColumn Header="路径" Binding="{Binding Path}" Width="3*"/>
                <DataGridCheckBoxColumn Header="白名单" Binding="{Binding IsExcluded}" Width="*"/>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</ui:Page>
```

列头硬编码处同样补 i18n key(`Processes.Col.Name` 等已在词典)。页面 Loaded 时执行 `RefreshCommand`。

- [ ] **Step 3: 白名单页(列表 + 移除 + 导入/导出)**

`WhitelistViewModel`:`ObservableCollection<string> Items`(读 `Locator.Whitelist.Excluded`)、`RemoveCommand(string)`、`ImportCommand`(OpenFileDialog → `Whitelist.Import`)、`ExportCommand`(SaveFileDialog → `Whitelist.Export`)。XAML:ListView + 每行删除按钮 + 顶部导入/导出按钮 + 空态文案 `Whitelist.Empty`。

- [ ] **Step 4: 运行验证(手工)**

Expected: 进程列表按内存降序;右键加入白名单后白名单页出现该项;移除/导入/导出可用;`csrss` 等系统进程显示为受保护(右键禁加,勾选项灰显)。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "进程页与白名单页:排行、右键加白、增删导入导出、系统进程保护展示"
```

---

### Task 14: 规则页 + 设置页(FR-3、FR-9、FR-10.9)

**Files:**
- Create: `src/AiMemoryManager/Views/RulesPage.xaml(.cs)`, `src/AiMemoryManager/ViewModels/RulesViewModel.cs`, `src/AiMemoryManager/Views/SettingsPage.xaml(.cs)`, `src/AiMemoryManager/ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: RulesViewModel — 直接双向绑定 Settings.Current 各字段,任何变更即 `Locator.Settings.Save()`**

```csharp
public partial class RulesViewModel : ObservableObject
{
    public AppSettings S => Locator.Settings.Current;
    public RulesViewModel() => Locator.Settings.SettingsSaved += (_, _) => OnPropertyChanged(nameof(S));
    [RelayCommand] private void Save() => Locator.Settings.Save();
}
```

XAML:`ui:Card` 内两组:
- 阈值卡:`ToggleSwitch`(ThresholdRuleEnabled)+ `NumberBox`(ThresholdPercent, 40–95)+ `NumberBox`(SustainSeconds, 10–300),文案 `Rules.Threshold`/`Rules.SustainSuffix` 拼接
- 定时卡:`ToggleSwitch`(TimerRuleEnabled)+ `NumberBox`(TimerIntervalMinutes)
- 两个独立开关:`ToggleSwitch`(AutoCleanIncludeL2,文案 `Rules.IncludeL2`)、`ToggleSwitch`(OnlyWhenNotFullscreen,文案 `Rules.NoFullscreen`)
- 所有控件 `LostFocus`/`Toggled` 事件触发 `SaveCommand`(或绑定 UpdateSourceTrigger=PropertyChanged + PropertyChanged 里 Save)。

- [ ] **Step 2: SettingsViewModel**

```csharp
public partial class SettingsViewModel : ObservableObject
{
    public List<string> Languages { get; } = new() { "auto", "zh-CN", "en" };
    public string Language
    {
        get => Locator.Settings.Current.Language;
        set
        {
            Locator.Settings.Current.Language = value;
            if (value == "auto") Locator.L10n.SetAuto();
            else Locator.L10n.CurrentLanguage = value;
            Locator.Settings.Save();
            OnPropertyChanged();
        }
    }
    public bool Animations
    {
        get => Locator.Settings.Current.Animations;
        set { Locator.Settings.Current.Animations = value; Locator.Settings.Save(); OnPropertyChanged(); }
    }
}
```

XAML:语言 `ComboBox`(三项:跟随系统/中文/English)、动效 `ToggleSwitch`、关于卡(版本号)。切换语言即时生效(LocalizationService 已触发 `Item[]` 刷新,所有绑定自动更新)。

- [ ] **Step 3: 运行验证(手工)**

Expected: 规则改动写入 settings.json;设置页切英文,全部界面文案即时变英文,无需重启。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "规则页与设置页:阈值/定时规则配置、语言即时切换、动效开关"
```

---

### Task 15: 托盘(FR-8.1/8.2)+ 最小化到托盘

**Files:**
- Create: `src/AiMemoryManager/Services/TrayIconRenderer.cs`
- Modify: `src/AiMemoryManager/MainWindow.xaml(.cs)`

**Interfaces:**
- Produces:
```csharp
public static class TrayIconRenderer
{
    public static System.Drawing.Icon Render(double percent); // 32x32,圆环+百分比数字
}
```

- [ ] **Step 1: 实现 TrayIconRenderer(System.Drawing)**

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AiMemoryManager.Services;

public static class TrayIconRenderer
{
    public static Icon Render(double percent)
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var color = percent >= 85 ? Color.OrangeRed : percent >= 60 ? Color.Orange : Color.ForestGreen;
            using var trackPen = new Pen(Color.Gray, 4);
            using var arcPen = new Pen(color, 4);
            g.DrawEllipse(trackPen, 3, 3, 26, 26);
            g.DrawArc(arcPen, 3, 3, 26, 26, -90, (float)(percent / 100 * 360));
            using var font = new Font("Segoe UI", percent >= 100 ? 9f : 10f, FontStyle.Bold);
            var text = ((int)Math.Round(percent)).ToString();
            var size = g.MeasureString(text, font);
            using var brush = new SolidBrush(Color.White);
            // 深色/浅色托盘兼容:白字+深色描边
            using var outline = new Pen(Color.Black, 2);
            g.DrawString(text, font, Brushes.Black, 16 - size.Width / 2 + 1, 16 - size.Height / 2 + 1);
            g.DrawString(text, font, brush, 16 - size.Width / 2, 16 - size.Height / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
```

> 注意 `GetHicon` 句柄需在图标替换时 `DestroyIcon`(user32)防泄漏;M1 每 2 秒换一个图标,加一个释放辅助方法并在替换处调用。

- [ ] **Step 2: MainWindow 集成 TaskbarIcon(H.NotifyIcon)**

```xml
<!-- MainWindow.xaml 根元素内加 xmlns:tb="http://www.hardcodet.net/taskbar" -->
<tb:TaskbarIcon x:Name="Tray" ToolTipText="AI Memory Manager" TrayMouseDoubleClick="OnTrayOpen">
    <tb:TaskbarIcon.ContextMenu>
        <ContextMenu>
            <MenuItem Header="一键清理" Click="OnTrayClean"/>
            <MenuItem Header="打开主界面" Click="OnTrayOpen"/>
            <Separator/>
            <MenuItem Header="退出" Click="OnTrayExit"/>
        </ContextMenu>
    </tb:TaskbarIcon.ContextMenu>
</tb:TaskbarIcon>
```

`MainWindow.xaml.cs`:
```csharp
// 菜单 Header 改为绑定 L10n(Task 14 模式),此处先列事件处理:
private void OnTrayOpen(object s, RoutedEventArgs e)
{ Show(); WindowState = WindowState.Normal; Activate(); }

private async void OnTrayClean(object s, RoutedEventArgs e)
{
    var r = await Services.Locator.Clean.RunL1Async(Models.CleanTrigger.Tray);
    Tray.ShowNotification("AI Memory Manager",
        string.Format(Services.Locator.L10n["Clean.Done"], r.FreedBytes / (1 << 20)));
}

private void OnTrayExit(object s, RoutedEventArgs e)
{ _reallyClose = true; Close(); }

private bool _reallyClose;
protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
{
    if (!_reallyClose) { e.Cancel = true; Hide(); }   // 关闭即最小化到托盘
    base.OnClosing(e);
}

// 图标刷新:订阅 Monitor.Sampled
// Tray.Icon = TrayIconRenderer.Render(info.UsedPercent);(先 DestroyIcon 旧句柄)
// ToolTipText = $"内存占用 {info.UsedPercent:F0}%";(进 i18n)
```

- [ ] **Step 3: 运行验证(手工)**

Expected: 托盘图标显示实时百分比(绿/橙/红分级);双击/右键菜单可用;一键清理弹通知;点 X 收进托盘,"退出"才真正退出。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "托盘:动态百分比图标、右键菜单、清理通知、关闭最小化到托盘"
```

---

### Task 16: 集成收尾 — Helper 复制、动效开关落地、L2 首用引导

**Files:**
- Modify: `src/AiMemoryManager/AiMemoryManager.csproj`(复制 Helper 产物), `src/AiMemoryManager/Views/DashboardPage.xaml.cs`(L2 失败文案/首次 UAC 说明), `src/AiMemoryManager/App.xaml.cs`(动效开关)

- [ ] **Step 1: csproj 引用 Helper 并复制产物**

```xml
<ItemGroup>
  <ProjectReference Include="..\AiMemoryManager.ElevatedHelper\AiMemoryManager.ElevatedHelper.csproj">
    <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
    <OutputItemType>Content</OutputItemType>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </ProjectReference>
</ItemGroup>
```

构建后确认输出目录有 `AiMemoryManager.ElevatedHelper.exe`。

- [ ] **Step 2: 动效开关落地**

`AnimationsEnabled=false` 时:圆环 `Percent` 更新直接赋值(默认即无动画,此项主要控制 MiniChart 刷新与后续 Storyboard)。M1 落法:在 `DashboardViewModel.Refresh()` 中若关动效则跳过曲线重建动画——M1 曲线本是无动画 Polyline,故本步只需把设置项接到"清理按钮按下反馈"等仅有的 Storyboard 上;**若 M1 界面无 Storyboard,本步退化为**:在 `SettingsPage` 标注"动效开关将在后续版本影响更多动画",保持设置项持久化即可(不伪造功能)。

- [ ] **Step 3: L2 首用引导**

仪表盘 `CleanL2Command` 失败时(用户取消 UAC / 注册失败),`LastCleanText` 显示友好文案(进 i18n):`"需要一次管理员授权才能深度清理,授权后不再询问"`。

- [ ] **Step 4: 全量回归**

Run: `dotnet test` → 全部通过;`dotnet run --project src/AiMemoryManager` 手工走查:
- [ ] 仪表盘实时数据/清理/曲线
- [ ] 规则:阈值 1% 触发自动清理(临时调低验证)、全屏抑制、定时规则
- [ ] 白名单全链路
- [ ] 托盘全链路
- [ ] 中英文即时切换
- [ ] 设置重启后保留

- [ ] **Step 5: Commit + 推送**

```bash
git add -A
git commit -m "M1收尾:Helper产物打包、L2首用引导文案、全量回归"
git push
```

---

## Self-Review 记录

- **Spec 覆盖**:FR-1.1/1.2/1.3 → Task 2/12/13;FR-2.1/2.2/2.4 → Task 6/7/12/16;FR-3.1~3.4 → Task 8/14;FR-7.1/7.3/7.4/7.5 → Task 4/5/13;FR-8.1/8.2 → Task 15;FR-9.1/9.2 → Task 9/14;FR-10.1~10.7 → Task 11/12(Mica/导航/卡片/英雄区/留白配色);L2 免 UAC → Task 6。FR-2.5(历史)、FR-8.3+(自启/通知/热键)属 M3,不在本计划。
- **已知妥协**:Task 6 的 `SYSTEM_PERFORMANCE_INFORMATION` 字段偏移需 Step 4 运行校验修正;WPF-UI 版本 API 差异以包内 IntelliSense 为准;Task 16 Step 2 动效开关按"不伪造功能"原则退化处理。
- **类型一致性**:`CleanService` 第三参数为 `IL2Executor` 接口(测试 FakeL2 / 生产 ElevatedL2Service 均实现);`RuleEngine.TickIntervalSeconds=10` 与测试用例的 SustainSeconds 换算一致。
