using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class ProcessTerminateService
{
    private readonly INativeMemoryApi _native;
    private readonly WhitelistService _whitelist;
    private readonly ForegroundGuard _guard;
    private readonly UnsavedStateDetector _detector;   // 预留:确认对话框在 UI 层用,执行端不重复
    private readonly KillLogService _killLog;

    public event EventHandler<TerminateResult>? TerminateCompleted;

    public ProcessTerminateService(INativeMemoryApi native, WhitelistService whitelist,
        ForegroundGuard guard, UnsavedStateDetector detector, KillLogService killLog)
        => (_native, _whitelist, _guard, _detector, _killLog) = (native, whitelist, guard, detector, killLog);

    public IReadOnlyList<int> FilterCandidates(IReadOnlyCollection<int> pids)
    {
        return FilterCandidates(_native.GetProcessSnapshots(), pids);
    }

    /// <summary>
    /// Filters against an already collected sample. The process page uses this
    /// overload so a refresh does not enumerate every process a second time.
    /// </summary>
    public IReadOnlyList<int> FilterCandidates(
        IReadOnlyCollection<ProcessSnapshot> snapshots,
        IReadOnlyCollection<int>? pids = null)
    {
        var names = snapshots.ToDictionary(p => p.Pid, p => p.Name);
        var candidates = pids ?? snapshots.Select(p => p.Pid).ToArray();
        var foregroundPid = _native.GetForegroundPid();
        return candidates.Where(pid =>
        {
            if (!names.TryGetValue(pid, out var name)) return false;
            if (_guard.IsProtected(pid, foregroundPid)) return false;
            if (_whitelist.IsSystemCritical(name)) return false;
            if (_whitelist.IsExcluded(name)) return false;
            if (_whitelist.IsNoKill(name)) return false;
            return true;
        }).ToList();
    }

    /// <summary>黑名单/对话计划使用的自动路径：白名单只代表清理排除，不覆盖用户明确的黑名单动作。</summary>
    public IReadOnlyList<int> FilterAutomaticCandidates(IReadOnlyCollection<int> pids)
    {
        var names = _native.GetProcessSnapshots().ToDictionary(p => p.Pid, p => p.Name);
        return pids.Where(pid =>
        {
            if (!names.TryGetValue(pid, out var name)) return false;
            if (pid == Environment.ProcessId) return false;
            if (_whitelist.IsSystemCritical(name)) return false;
            if (_whitelist.IsNoKill(name)) return false;
            if (_guard.IsProtected(pid)) return false;
            return true;
        }).ToList();
    }

    public Task<TerminateResult> TerminateAsync(IReadOnlyCollection<int> pids)
        => TerminateCoreAsync(pids, FilterCandidates, "Manual");

    public Task<TerminateResult> TerminateAutomaticAsync(IReadOnlyCollection<int> pids, string source = "Blacklist")
        => TerminateCoreAsync(pids, FilterAutomaticCandidates, source);

    private Task<TerminateResult> TerminateCoreAsync(
        IReadOnlyCollection<int> pids,
        Func<IReadOnlyCollection<int>, IReadOnlyList<int>> filter,
        string source)
        => Task.Run(() =>
        {
            var snaps = _native.GetProcessSnapshots().ToDictionary(p => p.Pid);
            var items = new List<TerminateItemResult>();
            long freed = 0;
            foreach (var pid in filter(pids))
            {
                if (!snaps.TryGetValue(pid, out var snap)) continue;
                bool ok = _native.TryTerminateProcess(pid, out int err);
                items.Add(new TerminateItemResult(pid, snap.Name, ok, ok ? 0 : err));
                if (ok)
                {
                    freed += snap.WorkingSetBytes;
                    _killLog.Record(new KillRecord(DateTimeOffset.Now, pid, snap.Name, snap.Path, null)
                    {
                        Source = source
                    });
                }
            }
            var result = new TerminateResult(freed, items);
            TerminateCompleted?.Invoke(this, result);
            return result;
        });
}