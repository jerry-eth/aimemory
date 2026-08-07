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

    // 不可变快照:CleanService 在线程池线程上枚举白名单,UI 线程可能并发 Add/Remove/Import,
    // 直接读 live List 存在枚举竞态。每次变更后整体重建 HashSet,读端无锁且永不见半更新状态。
    private volatile HashSet<string> _excludedSnapshot;

    public WhitelistService(SettingsService settings)
    {
        _settings = settings;
        _excludedSnapshot = BuildSnapshot();
    }

    public IReadOnlyCollection<string> Excluded => _settings.Current.ExcludedProcesses;

    public bool IsExcluded(string processName) =>
        _excludedSnapshot.Contains(NormalizeName(processName));

    public bool IsSystemCritical(string processName) =>
        SystemCritical.Contains(NormalizeName(processName));

    public void Add(string processName)
    {
        var n = NormalizeName(processName);
        if (n.Length == 0 || Excluded.Contains(n)) return;
        _settings.Current.ExcludedProcesses.Add(n);
        _settings.Save();
        _excludedSnapshot = BuildSnapshot();
    }

    public void Remove(string processName)
    {
        _settings.Current.ExcludedProcesses.Remove(NormalizeName(processName));
        _settings.Save();
        _excludedSnapshot = BuildSnapshot();
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
        _excludedSnapshot = BuildSnapshot();
    }

    public void Export(string filePath) =>
        File.WriteAllLines(filePath, Excluded.Select(n => n + ".exe"));

    private HashSet<string> BuildSnapshot() =>
        new(_settings.Current.ExcludedProcesses, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return n.EndsWith(".exe") ? n[..^4] : n;
    }
}
