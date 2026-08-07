using System.IO;

namespace AiMemoryManager.Services;

public class WhitelistService
{
    private static readonly HashSet<string> SystemCritical = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "svchost", "dwm", "explorer", "sihost", "taskhostw", "ctfmon",
        "securityhealthservice", "msmpeng", "memory compression", "system idle process"
    };

    private readonly SettingsService _settings;
    public WhitelistService(SettingsService settings) => _settings = settings;

    public IReadOnlyCollection<string> Excluded => _settings.Current.ExcludedProcesses;

    public bool IsExcluded(string processName) =>
        Excluded.Contains(NormalizeName(processName));

    public bool IsSystemCritical(string processName) =>
        SystemCritical.Contains(NormalizeName(processName));

    public void Add(string processName)
    {
        var n = NormalizeName(processName);
        if (n.Length == 0 || Excluded.Contains(n)) return;
        _settings.Current.ExcludedProcesses.Add(n);
        _settings.Save();
    }

    public void Remove(string processName)
    {
        _settings.Current.ExcludedProcesses.Remove(NormalizeName(processName));
        _settings.Save();
    }

    public void Import(string filePath)
    {
        foreach (var line in File.ReadAllLines(filePath))
        {
            var n = NormalizeName(line);
            if (n.Length > 0 && !Excluded.Contains(n))
                _settings.Current.ExcludedProcesses.Add(n);
        }
        _settings.Save();
    }

    public void Export(string filePath) =>
        File.WriteAllLines(filePath, Excluded.Select(n => n + ".exe"));

    private static string NormalizeName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return n.EndsWith(".exe") ? n[..^4] : n;
    }
}
