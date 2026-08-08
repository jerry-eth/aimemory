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
                Current.ExcludedProcesses ??= new();
                return;
            }
        }
        catch { /* 损坏 → 默认值 */ }
        Current = new AppSettings();
        Save();
    }

    public void Save()
    {
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOpts));
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }
}
