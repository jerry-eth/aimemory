using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class CleanHistoryService
{
    public const int Capacity = 100;

    private readonly string _path;
    private readonly LinkedList<CleanHistoryEntry> _entries = new();

    public IReadOnlyList<CleanHistoryEntry> Entries => _entries.ToList();
    public event EventHandler? Changed;

    public CleanHistoryService(string filePath)
    {
        _path = filePath;
        try
        {
            if (File.Exists(_path))
                foreach (var e in JsonSerializer.Deserialize<List<CleanHistoryEntry>>(File.ReadAllText(_path)) ?? new())
                    _entries.AddLast(e);
        }
        catch { /* 损坏 → 空 */ }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "clean-history.json");

    public void Record(CleanHistoryEntry entry)
    {
        _entries.AddFirst(entry);
        while (_entries.Count > Capacity) _entries.RemoveLast();
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_entries.ToList())); }
        catch { /* 写失败不致命 */ }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
