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

    /// <summary>泄漏告警转发:LeakDetection 在非 UI 线程触发,由 MainWindow 订阅后切回 UI 线程弹通知。</summary>
    public static event Action<LeakAlert>? LeakAlerted;

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

        // M2:30s 泄漏采样
        _leakTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _leakTimer.Tick += (_, _) =>
        {
            try { Locator.LeakDetection.Sample(Locator.Native.GetProcessSnapshots()); }
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

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ruleTimer?.Stop();
        _analysisTimer?.Stop();
        _leakTimer?.Stop();
        if (Locator.Monitor is not null) Locator.Monitor.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
