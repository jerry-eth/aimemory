using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AiMemoryManager.Services;

public class LocalizationService : INotifyPropertyChanged
{
    private readonly string _dir;
    private Dictionary<string, string> _dict = new();
    private string _lang = "zh-CN";

    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService(string i18nDir)
    {
        _dir = i18nDir;
        LoadIntoDict(_lang);
    }

    public string CurrentLanguage
    {
        get => _lang;
        set
        {
            if (_lang == value) return;
            _lang = value;
            LoadIntoDict(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public string this[string key] =>
        _dict.TryGetValue(key, out var v) ? v : key;

    public void SetAuto() =>
        CurrentLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en";

    private void LoadIntoDict(string lang)
    {
        var path = Path.Combine(_dir, lang + ".json");
        try { _dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(); }
        catch { _dict = new(); }
    }
}
