using AiMemoryManager.Models;
using AiMemoryManager.Native;

namespace AiMemoryManager.Services;

public class MemoryMonitorService : IDisposable
{
    private const int MaxSamples = 150;
    private readonly INativeMemoryApi _native;
    private readonly Timer _timer;
    private readonly List<double> _recent = new();
    private readonly object _sync = new();
    private readonly int _intervalMs;

    public event EventHandler<SystemMemoryInfo>? Sampled;
    public IReadOnlyList<double> RecentPercents { get { lock (_sync) return _recent.ToArray(); } }

    public MemoryMonitorService(INativeMemoryApi native, int intervalMs = 2000)
    {
        _native = native;
        _intervalMs = intervalMs;
        _timer = new Timer(_ => Sample(), null, Timeout.Infinite, intervalMs);
    }

    public void Start() => _timer.Change(0, _intervalMs);

    private void Sample()
    {
        var info = _native.GetSystemMemory();
        lock (_sync)
        {
            _recent.Add(info.UsedPercent);
            if (_recent.Count > MaxSamples) _recent.RemoveAt(0);
        }
        Sampled?.Invoke(this, info);
    }

    public void Dispose() => _timer.Dispose();
}
