using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class LlmProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SettingsService _settings;
    private readonly List<LlmProfile> _profiles = new();

    public IReadOnlyList<LlmProfile> Profiles => _profiles;

    public LlmProfileService(string filePath, SettingsService settings)
        => (_path, _settings) = (filePath, settings);

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "llm-profiles.json");

    public void Load()
    {
        _profiles.Clear();
        try
        {
            if (File.Exists(_path))
                _profiles.AddRange(
                    JsonSerializer.Deserialize<List<LlmProfile>>(File.ReadAllText(_path)) ?? new());
        }
        catch { /* 损坏 → 空 */ }
    }

    public void Save(LlmProfile profile)
    {
        var i = _profiles.FindIndex(p => p.Id == profile.Id);
        if (i >= 0) _profiles[i] = profile; else _profiles.Add(profile);
        Persist();
    }

    public void Delete(string id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        if (_settings.Current.ActiveProfileId == id)
        {
            _settings.Current.ActiveProfileId = null;
            _settings.Save();
        }
        Persist();
    }

    public LlmProfile? GetActive() =>
        _profiles.FirstOrDefault(p => p.Id == _settings.Current.ActiveProfileId);

    public void SetActive(string id)
    {
        _settings.Current.ActiveProfileId = id;
        _settings.Save();
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_profiles, JsonOpts));
    }
}
