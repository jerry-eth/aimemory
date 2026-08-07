using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class MemoryMonitorService : IDisposable
{
    private const int MaxSamples = 150;
    private readonly INativeMemoryApi _native;
    private readonly Timer _timer;
    private readonly List<double> _recent = new();

    public event EventHandler<SystemMemoryInfo>? Sampled;
    public IReadOnlyList<double> RecentPercents => _recent;

    public MemoryMonitorService(INativeMemoryApi native, int intervalMs = 2000)
    {
        _native = native;
        _timer = new Timer(_ => Sample(), null, Timeout.Infinite, intervalMs);
    }

    public void Start() => _timer.Change(0, 2000);

    private void Sample()
    {
        var info = _native.GetSystemMemory();
        lock (_recent)
        {
            _recent.Add(info.UsedPercent);
            if (_recent.Count > MaxSamples) _recent.RemoveAt(0);
        }
        Sampled?.Invoke(this, info);
    }

    public void Dispose() => _timer.Dispose();
}
