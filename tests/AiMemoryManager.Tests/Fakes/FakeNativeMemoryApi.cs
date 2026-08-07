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
}
