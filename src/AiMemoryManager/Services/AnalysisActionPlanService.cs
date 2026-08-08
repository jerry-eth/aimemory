using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public sealed class AnalysisActionPlanService
{
    public IReadOnlyList<ProcessSnapshot> ResolveTerminateTargets(AnalysisActionPlan plan)
    {
        if (plan.Operation != "terminate_processes") return Array.Empty<ProcessSnapshot>();
        var wanted = plan.Targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshots = Locator.Native.GetProcessSnapshots()
            .Where(p => wanted.Contains(Normalize(p.Name)))
            .ToList();
        var killable = Locator.Terminator.FilterAutomaticCandidates(snapshots.Select(p => p.Pid).ToList()).ToHashSet();
        return snapshots.Where(p => killable.Contains(p.Pid)).ToList();
    }

    public async Task<AnalysisActionExecutionResult> ExecuteAsync(AnalysisActionPlan plan,
        IReadOnlyCollection<int> confirmedPids, CancellationToken ct = default)
    {
        switch (plan.Operation)
        {
            case "clean_working_sets":
                var l1 = await Locator.Clean.RunL1Async(CleanTrigger.Conversation, ct);
                return new AnalysisActionExecutionResult(plan.Operation, l1.FreedBytes, l1.ProcessCount, 0);
            case "purge_standby":
                var l2 = await Locator.Clean.RunL2Async(CleanTrigger.Conversation, ct);
                return new AnalysisActionExecutionResult(plan.Operation, l2.FreedBytes, 0, 0);
            case "terminate_processes":
                var r = await Locator.Terminator.TerminateAutomaticAsync(confirmedPids, "Conversation");
                var ok = r.Items.Count(x => x.Success);
                var fail = r.Items.Count - ok;
                if (ok > 0)
                    Locator.History.Record(new CleanHistoryEntry(DateTimeOffset.Now, CleanLevel.L3,
                        r.FreedBytes, ok, CleanTrigger.Conversation));
                return new AnalysisActionExecutionResult(plan.Operation, r.FreedBytes, ok, fail);
            default:
                return new AnalysisActionExecutionResult("none", 0, 0, 0);
        }
    }

    private static string Normalize(string name)
    {
        var value = name.Trim();
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }
}
