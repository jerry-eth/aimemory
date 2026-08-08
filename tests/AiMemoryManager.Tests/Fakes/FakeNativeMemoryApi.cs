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

    public Func<int, (bool, int)>? TerminateBehavior { get; set; }
    public List<int> TerminatedPids { get; } = new();
    public Dictionary<int, List<string>> WindowTitles { get; } = new();

    public bool TryTerminateProcess(int pid, out int win32Error)
    {
        TerminatedPids.Add(pid);
        var (ok, err) = TerminateBehavior?.Invoke(pid) ?? (true, 0);
        win32Error = err;
        return ok;
    }

    public IReadOnlyList<string> GetWindowTitles(int pid) =>
        WindowTitles.TryGetValue(pid, out var titles) ? titles : new List<string>();
}
