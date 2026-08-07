using System.Windows;
using System.Windows.Threading;
using AiMemoryManager.Services;

namespace AiMemoryManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private DispatcherTimer? _ruleTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "AiMemoryManager.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }              // 单实例:第二个实例立即退出

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
