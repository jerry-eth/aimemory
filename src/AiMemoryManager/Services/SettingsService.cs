using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    public AppSettings Current { get; private set; } = new();
    public event EventHandler? SettingsSaved;

    public SettingsService(string path) => _path = path;

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "settings.json");

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
                Normalize(Current);
                return;
            }
        }
        catch { /* 损坏 → 默认值 */ }
        Current = new AppSettings();
        Normalize(Current);
        Save();
    }

    private static void Normalize(AppSettings settings)
    {
        // 配置文件可能来自旧版本或被用户手工编辑；集合不能为 null，数值也必须落在 UI/规则允许范围内。
        settings.ExcludedProcesses ??= new();
        settings.BlacklistProcesses ??= new();
        settings.NoKillProcesses ??= new();
        settings.Language = settings.Language switch
        {
            "auto" or "zh-CN" or "en" => settings.Language,
            _ => "auto"
        };
        settings.ThresholdPercent = Math.Clamp(settings.ThresholdPercent, 40, 95);
        settings.SustainSeconds = Math.Clamp(settings.SustainSeconds, 1, 3600);
        settings.TimerIntervalMinutes = Math.Clamp(settings.TimerIntervalMinutes, 1, 24 * 60);
        settings.LlmDailyCallCap = Math.Clamp(settings.LlmDailyCallCap, 0, 1000);
        settings.LlmTimerIntervalHours = Math.Clamp(settings.LlmTimerIntervalHours, 1, 24 * 30);
        settings.MonthlyTokenBudget = Math.Max(0, settings.MonthlyTokenBudget);
        settings.LeakGrowthThresholdMb = Math.Clamp(settings.LeakGrowthThresholdMb, 50, 10000);
        settings.LeakWindowMinutes = Math.Clamp(settings.LeakWindowMinutes, 5, 120);
        settings.HotkeyModifiers &= 0x0F;
        settings.HotkeyKey = Math.Clamp(settings.HotkeyKey, 1, 0xFF);
    }

    public void Save()
    {
        Normalize(Current);
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOpts));
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }
}
