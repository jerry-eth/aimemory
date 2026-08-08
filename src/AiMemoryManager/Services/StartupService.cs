using System.Diagnostics;
using Microsoft.Win32;

namespace AiMemoryManager.Services;

/// <summary>
/// FR-8.3 开机自启:HKCU Run 键读写(默认实现),测试注入委托避免真实注册表访问。
/// MSIX 打包态的 StartupTask 声明留待 M4,本机制面向未打包(注册表)场景。
/// </summary>
public class StartupService
{
    private const string RunKeyName = "AiMemoryManager";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly SettingsService _settings;
    private readonly Action<string, string?> _setRunKey;
    private readonly Func<string, string?> _getRunKey;

    public StartupService(SettingsService settings,
        Action<string, string?>? setRunKey = null, Func<string, string?>? getRunKey = null)
    {
        _settings = settings;
        _setRunKey = setRunKey ?? DefaultSet;
        _getRunKey = getRunKey ?? DefaultGet;
    }

    public bool IsEnabled => _getRunKey(RunKeyName) != null;

    public void SetEnabled(bool enabled)
    {
        try
        {
            _setRunKey(RunKeyName, enabled ? $"\"{Environment.ProcessPath}\"" : null);
            _settings.Current.AutoStartEnabled = enabled;
            _settings.Save();
        }
        catch (Exception ex) { Debug.WriteLine("StartupService: " + ex); }
    }

    private static void DefaultSet(string name, string? value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (value == null) key?.DeleteValue(name, throwOnMissingValue: false);
        else key?.SetValue(name, value);
    }

    private static string? DefaultGet(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(name)?.ToString();
    }
}
