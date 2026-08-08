using System.Windows;
using System.Windows.Threading;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>关机/注销时置 true,主窗口 OnClosing 不再取消关闭(否则系统提示"应用阻止关机")。</summary>
    internal static bool IsSessionEnding { get; private set; }

    /// <summary>泄漏告警转发:事件在 UI 线程触发(_leakTimer.Tick 内 Sample 始终由 Dispatcher 封送回 UI 线程),MainWindow 可直接弹通知。</summary>
    public static event Action<LeakAlert>? LeakAlerted;
    public static event Action<BlacklistActionResult>? BlacklistActioned;

    private Mutex? _mutex;
    private DispatcherTimer? _ruleTimer;
    private DispatcherTimer? _analysisTimer;
    private DispatcherTimer? _leakTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "AiMemoryManager.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }              // 单实例:第二个实例立即退出

        SessionEnding += (_, _) => IsSessionEnding = true; // 关机/注销:允许窗口真正关闭

        Locator.Init();
        Locator.ProcessStartMonitor.Actioned += (_, result) => BlacklistActioned?.Invoke(result);
        Locator.ProcessStartMonitor.MonitorError += (_, ex) => System.Diagnostics.Debug.WriteLine($"[Blacklist] Monitor failed: {ex.Message}");
        Locator.ProcessStartMonitor.SetEnabled(Locator.Settings.Current.BlacklistAutoTerminateEnabled);
        Locator.Monitor.Start();

        _ruleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RuleEngine.TickIntervalSeconds) };
        _ruleTimer.Tick += (_, _) =>
        {
            try { Locator.Rules.Tick(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Rules] Tick failed: {ex}"); }
        };
        _ruleTimer.Start();

        // M2:60s 调度 tick(阈值/定时自动分析判定)
        _analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _analysisTimer.Tick += async (_, _) =>
        {
            try { await Locator.Scheduler.TickAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Scheduler] Tick failed: {ex}"); }
        };
        _analysisTimer.Start();

        // M2:30s 泄漏采样。进程枚举较耗时,放线程池;Sample 内部非线程安全,await 后回到 UI 线程再调用
        _leakTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _leakTimer.Tick += async (_, _) =>
        {
            try
            {
                var snapshots = await Task.Run(() => Locator.Native.GetProcessSnapshots());
                Locator.LeakDetection.Sample(snapshots);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Leak] Sample failed: {ex}"); }
        };
        _leakTimer.Start();

        // 泄漏告警 → 事件转发,由 MainWindow 弹 Windows 通知
        Locator.LeakDetection.LeakDetected += (_, a) => LeakAlerted?.Invoke(a);

        Locator.Rules.CleanRequested += async (_, req) =>
        {
            // async void 事件处理器:清理失败(如 L2 助手缺失/UAC 取消)不能拖垮整个应用
            try
            {
                if (req.Level == Models.CleanLevel.L2) await Locator.Clean.RunL2Async(req.Trigger);
                else await Locator.Clean.RunL1Async(req.Trigger);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Rules] Auto clean failed: {ex}"); }
        };

        base.OnStartup(e);

        var mainWindow = new MainWindow();
        mainWindow.Show();

        // FR-8.5 全局热键:窗口句柄就绪后注册;被其他程序占用时静默降级,仅记录日志
        var hwnd = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
        if (hwnd != IntPtr.Zero &&
            !Locator.Hotkey.Register(hwnd, Locator.Settings.Current.HotkeyModifiers, Locator.Settings.Current.HotkeyKey))
            System.Diagnostics.Debug.WriteLine("[Hotkey] Register failed: hotkey occupied by another app");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ruleTimer?.Stop();
        _analysisTimer?.Stop();
        _leakTimer?.Stop();
        if (Locator.Hotkey is not null) Locator.Hotkey.Dispose();   // FR-8.5:退出前注销全局热键
        if (Locator.Monitor is not null) Locator.Monitor.Dispose();
        if (Locator.ProcessStartMonitor is not null) Locator.ProcessStartMonitor.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
