using System.Diagnostics;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;

namespace AiMemoryManager.Services;

/// <summary>
/// FR-8.3 开机自启：MSIX 打包态使用 StartupTask，未打包运行时回退到 HKCU Run。
/// StartupTask 的启用/禁用状态由系统管理；请求启用失败时不会静默写入错误路径。
/// </summary>
public class StartupService
{
    public const string StartupTaskId = "AiMemoryManagerStartup";
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

    /// <summary>当前进程是否运行在 MSIX 包身份下。</summary>
    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Package.Current.Id;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsEnabled
    {
        get
        {
            if (IsPackaged && TryGetPackagedState(out var state))
                return state == StartupTaskState.Enabled;
            return _getRunKey(RunKeyName) != null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (IsPackaged && TrySetPackagedEnabled(enabled))
            {
                _settings.Current.AutoStartEnabled = enabled;
                _settings.Save();
                return;
            }

            var processPath = Environment.ProcessPath;
            if (enabled && string.IsNullOrWhiteSpace(processPath))
                throw new InvalidOperationException("无法确定当前程序路径，不能配置开机自启。");
            _setRunKey(RunKeyName, enabled ? $"\"{processPath}\"" : null);
            _settings.Current.AutoStartEnabled = enabled;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("StartupService: " + ex);
        }
    }

    private static bool TryGetPackagedState(out StartupTaskState state)
    {
        state = StartupTaskState.Disabled;
        try
        {
            var task = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            state = task.State;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("StartupTask read failed: " + ex.Message);
            return false;
        }
    }

    private static bool TrySetPackagedEnabled(bool enabled)
    {
        try
        {
            var task = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            if (enabled)
            {
                var state = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
                return state == StartupTaskState.Enabled;
            }

            task.Disable();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("StartupTask write failed: " + ex.Message);
            return false;
        }
    }

    private static void DefaultSet(string name, string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null) throw new InvalidOperationException("无法打开当前用户的开机自启注册表项。");
        if (value == null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static string? DefaultGet(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(name)?.ToString();
    }
}
