using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class KillLogService
{
    public const int Capacity = 20;

    private readonly string _path;
    private readonly Func<int, string?> _cmdline;
    private readonly Action<string, string?> _starter;
    private readonly LinkedList<KillRecord> _records = new();

    public IReadOnlyList<KillRecord> Records => _records.ToList();

    public KillLogService(string filePath, Func<int, string?>? commandLineProvider = null, Action<string, string?>? starter = null)
    {
        _path = filePath;
        _cmdline = commandLineProvider ?? WmiCommandLine;
        _starter = starter ?? DefaultStart;
        try
        {
            if (File.Exists(_path))
                foreach (var r in JsonSerializer.Deserialize<List<KillRecord>>(File.ReadAllText(_path)) ?? new())
                    _records.AddLast(r);
        }
        catch { }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "kill-log.json");

    public void Record(KillRecord record)
    {
        string? args = record.Arguments;
        if (args == null)
        {
            try { args = _cmdline(record.Pid); } catch { args = null; }
        }
        _records.AddFirst(record with { Arguments = args });
        while (_records.Count > Capacity) _records.RemoveLast();
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_records.ToList())); } catch { }
    }

    public bool Restart(KillRecord record)
    {
        if (string.IsNullOrEmpty(record.Path) || !File.Exists(record.Path)) return false;
        try { _starter(record.Path, record.Arguments); return true; }
        catch { return false; }
    }

    private static void DefaultStart(string path, string? args) =>
        Process.Start(new ProcessStartInfo(path) { Arguments = args ?? "", UseShellExecute = true });

    private static string? WmiCommandLine(int pid)
    {
        // System.Management 包(Task 4 Step 3 先 dotnet add):select CommandLine from Win32_Process
        using var searcher = new System.Management.ManagementObjectSearcher(
            $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
        foreach (var mo in searcher.Get())
            return mo["CommandLine"]?.ToString();
        return null;
    }
}
