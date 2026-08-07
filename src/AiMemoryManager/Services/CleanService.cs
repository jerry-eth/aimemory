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
