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
        var names = _native.GetProcessSnapshots().ToDictionary(p => p.Pid, p => p.Name);
        return pids.Where(pid =>
        {
            if (!names.TryGetValue(pid, out var name)) return false;
            if (_whitelist.IsSystemCritical(name)) return false;
            if (_whitelist.IsExcluded(name)) return false;
            if (_whitelist.IsNoKill(name)) return false;
            if (_guard.IsProtected(pid)) return false;
            return true;
        }).ToList();
    }

    public Task<TerminateResult> TerminateAsync(IReadOnlyCollection<int> pids)
        => Task.Run(() =>
        {
            var snaps = _native.GetProcessSnapshots().ToDictionary(p => p.Pid);
            var items = new List<TerminateItemResult>();
            long freed = 0;
            foreach (var pid in FilterCandidates(pids))
            {
                var snap = snaps[pid];
                bool ok = _native.TryTerminateProcess(pid, out int err);
                items.Add(new TerminateItemResult(pid, snap.Name, ok, ok ? 0 : err));
                if (ok)
                {
                    freed += snap.WorkingSetBytes;
                    _killLog.Record(new KillRecord(DateTimeOffset.Now, pid, snap.Name, snap.Path, null));
                }
            }
            var result = new TerminateResult(freed, items);
            TerminateCompleted?.Invoke(this, result);
            return result;
        });
}
