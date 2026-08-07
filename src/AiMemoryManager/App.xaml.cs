using System.Windows;
using System.Windows.Threading;
using AiMemoryManager.Services;

namespace AiMemoryManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>关机/注销时置 true,主窗口 OnClosing 不再取消关闭(否则系统提示"应用阻止关机")。</summary>
    internal static bool IsSessionEnding { get; private set; }

    private Mutex? _mutex;
    private DispatcherTimer? _ruleTimer;

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
        if (Locator.Monitor is not null) Locator.Monitor.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
