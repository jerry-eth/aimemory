using System.Management;
using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public sealed record BlacklistActionResult(
    DateTimeOffset Time,
    int Pid,
    string ProcessName,
    string? Path,
    string Status,
    string Reason,
    TerminateResult? Termination);

/// <summary>后台监听 Win32_ProcessStartTrace，命中黑名单后走安全自动终止路径。</summary>
public sealed class ProcessStartMonitorService : IDisposable
{
    private readonly BlacklistService _blacklist;
    private readonly ProcessTerminateService _terminator;
    private readonly INativeMemoryApi _native;
    private readonly WhitelistService _whitelist;
    private readonly object _sync = new();
    private readonly HashSet<int> _inFlight = new();
    private ManagementEventWatcher? _watcher;
    private bool _enabled;
    private bool _disposed;

    public event EventHandler<BlacklistActionResult>? Actioned;
    public event EventHandler<Exception>? MonitorError;
    public bool IsEnabled { get { lock (_sync) return _enabled; } }

    public ProcessStartMonitorService(BlacklistService blacklist,
        ProcessTerminateService terminator, INativeMemoryApi native, WhitelistService whitelist)
        => (_blacklist, _terminator, _native, _whitelist) = (blacklist, terminator, native, whitelist);

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (enabled) StartWatcherLocked();
            else StopWatcherLocked();
        }
    }

    private void StartWatcherLocked()
    {
        try
        {
            if (_watcher is not null) return;
            _watcher = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += OnProcessStarted;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            _watcher?.Dispose();
            _watcher = null;
            _enabled = false;
            MonitorError?.Invoke(this, ex);
        }
    }

    private void StopWatcherLocked()
    {
        if (_watcher is null) return;
        try { _watcher.EventArrived -= OnProcessStarted; _watcher.Stop(); }
        catch { }
        finally { _watcher.Dispose(); _watcher = null; }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"]?.Value ?? 0);
            string name = e.NewEvent.Properties["ProcessName"]?.Value?.ToString() ?? "";
            if (pid <= 0 || !_blacklist.IsBlacklisted(name)) return;
            lock (_sync)
            {
                if (!_enabled || !_inFlight.Add(pid)) return;
            }
            _ = HandleAsync(pid, name);
        }
        catch (Exception ex) { MonitorError?.Invoke(this, ex); }
    }

    private async Task HandleAsync(int pid, string eventName)
    {
        try
        {
            var snapshot = _native.GetProcessSnapshots().FirstOrDefault(p => p.Pid == pid);
            if (snapshot is null)
            {
                Publish(pid, eventName, null, "skipped", "进程启动后已退出", null);
                return;
            }

            if (!_blacklist.IsBlacklisted(snapshot.Name))
            {
                Publish(pid, snapshot.Name, snapshot.Path, "skipped", "进程名已不在黑名单", null);
                return;
            }

            var candidates = _terminator.FilterAutomaticCandidates(new[] { pid });
            if (candidates.Count == 0)
            {
                string reason;
                if (pid == Environment.ProcessId) reason = "软件自身进程受保护";
                else if (_whitelist.IsSystemCritical(snapshot.Name)) reason = "系统关键进程受保护";
                else if (_whitelist.IsNoKill(snapshot.Name)) reason = "防误杀名单受保护";
                else reason = "前台或受保护进程";
                Publish(pid, snapshot.Name, snapshot.Path, "skipped", reason, null);
                return;
            }

            var result = await _terminator.TerminateAutomaticAsync(candidates, "Blacklist");
            var item = result.Items.FirstOrDefault(i => i.Pid == pid);
            Publish(pid, snapshot.Name, snapshot.Path,
                item?.Success == true ? "terminated" : "failed",
                item?.Success == true ? "已按黑名单自动终止" : $"终止失败，错误码 {item?.Win32Error}", result);
        }
        catch (Exception ex)
        {
            Publish(pid, eventName, null, "failed", ex.Message, null);
        }
        finally
        {
            lock (_sync) _inFlight.Remove(pid);
        }
    }

    private void Publish(int pid, string name, string? path, string status, string reason, TerminateResult? result) =>
        Actioned?.Invoke(this, new BlacklistActionResult(DateTimeOffset.Now, pid, name, path, status, reason, result));

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            StopWatcherLocked();
            _inFlight.Clear();
        }
    }
}