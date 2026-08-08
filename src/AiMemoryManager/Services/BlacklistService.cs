using System.Diagnostics;

namespace AiMemoryManager.Services;

/// <summary>黑名单服务：独立于内存清理白名单，仅用于启动后自动终止规则。</summary>
public sealed class BlacklistService
{
    private readonly SettingsService _settings;
    private volatile HashSet<string> _snapshot;

    public BlacklistService(SettingsService settings)
    {
        _settings = settings;
        _snapshot = BuildSnapshot();
    }

    public IReadOnlyCollection<string> Items => _settings.Current.BlacklistProcesses;

    public bool IsBlacklisted(string processName) =>
        _snapshot.Contains(NormalizeName(processName));

    public bool TryAdd(string processName, out string reason)
    {
        var name = NormalizeName(processName);
        if (name.Length == 0)
        {
            reason = "进程名不能为空";
            return false;
        }

        if (IsSystemCritical(name))
        {
            reason = "系统关键进程不能加入黑名单";
            return false;
        }

        if (string.Equals(name, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            reason = "软件自身不能加入黑名单";
            return false;
        }

        if (_settings.Current.NoKillProcesses.Any(n =>
                string.Equals(NormalizeName(n), name, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "防误杀名单中的进程不能加入黑名单";
            return false;
        }

        if (!_settings.Current.BlacklistProcesses.Any(n =>
                string.Equals(NormalizeName(n), name, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.Current.BlacklistProcesses.Add(name);
            _settings.Save();
            _snapshot = BuildSnapshot();
        }

        reason = "";
        return true;
    }

    public void Remove(string processName)
    {
        var name = NormalizeName(processName);
        _settings.Current.BlacklistProcesses.RemoveAll(n =>
            string.Equals(NormalizeName(n), name, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        _snapshot = BuildSnapshot();
    }

    public static string NormalizeName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        return normalized.EndsWith(".exe", StringComparison.Ordinal)
            ? normalized[..^4]
            : normalized;
    }

    private static bool IsSystemCritical(string processName) =>
        WhitelistService.SystemCriticalProcessNames.Contains(NormalizeName(processName));

    private HashSet<string> BuildSnapshot() =>
        new(_settings.Current.BlacklistProcesses.Select(NormalizeName), StringComparer.OrdinalIgnoreCase);
}