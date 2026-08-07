using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class LeakDetectionService
{
    private const int MaxAlerts = 50;
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromHours(2);

    private sealed class Track
    {
        public LinkedList<(DateTimeOffset Time, long Bytes)> Samples = new();
        public DateTimeOffset LastAlert = DateTimeOffset.MinValue;
    }

    private readonly SettingsService _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<int, Track> _tracks = new();
    private readonly LinkedList<LeakAlert> _alerts = new();

    public event EventHandler<LeakAlert>? LeakDetected;
    public IReadOnlyList<LeakAlert> RecentAlerts => _alerts.ToList();

    public LeakDetectionService(SettingsService settings, Func<DateTimeOffset> clock)
        => (_settings, _clock) = (settings, clock);

    public void Sample(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        var s = _settings.Current;
        if (!s.LeakDetectionEnabled) { _tracks.Clear(); return; }
        var now = _clock();
        long threshold = (long)s.LeakGrowthThresholdMb << 20;
        var window = TimeSpan.FromMinutes(s.LeakWindowMinutes);
        // 样本保留窗口 = 观察窗 + 15 分钟余量,保证窗口内的首个样本不被提前丢弃
        var keepWindow = window + TimeSpan.FromMinutes(15);

        var seen = new HashSet<int>();
        foreach (var snap in snapshots)
        {
            if (snap.WorkingSetBytes < 50L << 20) continue;   // 小进程不看
            seen.Add(snap.Pid);
            if (!_tracks.TryGetValue(snap.Pid, out var t))
                t = _tracks[snap.Pid] = new Track();

            // 回落 → 重置观察窗
            if (t.Samples.Last?.Value.Bytes > snap.WorkingSetBytes)
                t.Samples.Clear();
            t.Samples.AddLast((now, snap.WorkingSetBytes));
            while (t.Samples.First != null && now - t.Samples.First.Value.Time > keepWindow)
                t.Samples.RemoveFirst();

            var first = t.Samples.First!.Value;
            var last = t.Samples.Last!.Value;
            long growth = last.Bytes - first.Bytes;
            if (growth > threshold && last.Time - first.Time >= window
                && now - t.LastAlert >= AlertCooldown)
            {
                t.LastAlert = now;
                var alert = new LeakAlert(snap.Pid, snap.Name, growth, last.Time - first.Time, now);
                _alerts.AddFirst(alert);
                while (_alerts.Count > MaxAlerts) _alerts.RemoveLast();
                LeakDetected?.Invoke(this, alert);
            }
        }
        // 退出进程清轨
        foreach (var pid in _tracks.Keys.Where(k => !seen.Contains(k)).ToList())
            _tracks.Remove(pid);
    }
}
