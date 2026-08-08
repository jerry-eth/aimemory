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
            // WMI CommandLine 含 exe 自身路径,只保留参数部分;否则 Restart 会把 exe 路径当文档再传一遍
            try { args = StripExecutable(_cmdline(record.Pid)); } catch { args = null; }
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

    /// <summary>
    /// 去掉命令行首 token(exe 自身路径),只保留参数;无参数时返回 null。
    /// 处理引号路径:"C:\a b\x.exe" /fast → /fast;裸 exe(无空格)视为无参数。
    /// </summary>
    public static string? StripExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        commandLine = commandLine.TrimStart();
        int end;
        if (commandLine[0] == '"')
        {
            end = commandLine.IndexOf('"', 1);
            if (end < 0) return null;   // 引号未闭合:整条视为 exe 路径,无参数
            end++;
        }
        else
        {
            end = commandLine.IndexOf(' ');
            if (end < 0) return null;   // 裸 exe 无参数
        }
        var rest = commandLine[end..].Trim();
        return rest.Length == 0 ? null : rest;
    }

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
