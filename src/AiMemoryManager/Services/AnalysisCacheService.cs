using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class AnalysisCacheService
{
    public static TimeSpan Ttl { get; } = TimeSpan.FromHours(24);

    private record Entry(DateTimeOffset Time, List<AnalysisSuggestion> Suggestions);

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private Dictionary<string, Entry> _entries = new();

    public AnalysisCacheService(string filePath, Func<DateTimeOffset> clock)
    {
        (_path, _clock) = (filePath, clock);
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? new();
        }
        catch { _entries = new(); }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "analysis-cache.json");

    public bool TryGet(string hash, out IReadOnlyList<AnalysisSuggestion> suggestions)
    {
        if (_entries.TryGetValue(hash, out var e) && _clock() - e.Time < Ttl)
        {
            suggestions = e.Suggestions;
            return true;
        }
        suggestions = Array.Empty<AnalysisSuggestion>();
        return false;
    }

    public void Store(string hash, IReadOnlyList<AnalysisSuggestion> suggestions)
    {
        var now = _clock();
        foreach (var k in _entries.Where(kv => now - kv.Value.Time >= Ttl).Select(kv => kv.Key).ToList())
            _entries.Remove(k);
        _entries[hash] = new Entry(now, suggestions.ToList());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch { /* 缓存写失败不致命 */ }
    }
}
