using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

public sealed record LlmPreset(string DisplayName, string BaseUrl, string Model);

public partial class ProfileItemViewModel : ObservableObject
{
    public required LlmProfile Profile { get; init; }
    public string Name => Profile.Name;
    public string Model => Profile.Model;
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// 大模型设置页 VM:档案 CRUD + 测试连接/拉模型 + 触发配置(直读直写 Settings,setter 即 Save)+ 提示词模板管理。
/// 触发配置照 M1 RulesViewModel 模式:任何变更立即 Save,数值含 NaN 防护。
/// 表单明文 API Key 只存在于内存,保存/测试时才 DPAPI 加密,绝不回显已存密钥。
/// </summary>
public partial class LlmSettingsViewModel : ObservableObject
{
    private static AppSettings S => Locator.Settings.Current;

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = new();
    public ObservableCollection<string> ModelSuggestions { get; } = new();
    public ObservableCollection<LlmPreset> Presets { get; } = new();
    public ObservableCollection<PromptTemplate> Templates { get; } = new();

    // 编辑表单(新增/编辑共用)
    [ObservableProperty] private string _editId = "";
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editBaseUrl = "https://api.deepseek.com/v1";
    [ObservableProperty] private string _editApiKey = "";        // 明文仅存在于表单,保存即加密
    [ObservableProperty] private string _editModel = "";
    [ObservableProperty] private LlmPreset? _selectedPreset;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _isTesting;

    private double _editPrice;
    public double EditPrice
    {
        get => _editPrice;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = Math.Max(0, value);
            if (_editPrice == v) return;
            _editPrice = v;
            OnPropertyChanged();
        }
    }

    /// <summary>编辑态提示"留空则保持不变";新增态不提示。绝不回显已存密钥。</summary>
    public string ApiKeyPlaceholder =>
        string.IsNullOrEmpty(EditId) ? "" : Locator.L10n["Llm.KeyKeep"];

    partial void OnEditIdChanged(string value) => OnPropertyChanged(nameof(ApiKeyPlaceholder));

    public LlmSettingsViewModel()
    {
        LoadPresets();
        Refresh();
        ReloadTemplates();
    }

    private void LoadPresets()
    {
        Presets.Clear();
        Presets.Add(new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"));
        Presets.Add(new("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"));
        Presets.Add(new("Qwen / DashScope", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"));
        Presets.Add(new("Kimi / Moonshot", "https://api.moonshot.cn/v1", "moonshot-v1-8k"));
        Presets.Add(new("GLM / Zhipu", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"));
        Presets.Add(new("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini"));
        Presets.Add(new("SiliconFlow", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3"));
        Presets.Add(new("Ollama (Local)", "http://localhost:11434/v1", "qwen2.5:7b"));
    }

    [RelayCommand]
    private void ApplyPreset(LlmPreset? preset)
    {
        if (preset is null) return;
        EditBaseUrl = preset.BaseUrl;
        EditModel = preset.Model;
        EditPrice = 0;
        TestResult = "";
    }

    // ---------- 档案 ----------

    [RelayCommand]
    private void Refresh()
    {
        Profiles.Clear();
        var activeId = Locator.Profiles.GetActive()?.Id;
        foreach (var p in Locator.Profiles.Profiles)
            Profiles.Add(new ProfileItemViewModel { Profile = p, IsActive = p.Id == activeId });
    }

    [RelayCommand]
    private void NewProfile()
    {
        EditId = "";
        EditName = "";
        EditBaseUrl = "https://api.deepseek.com/v1";
        EditApiKey = "";
        EditModel = "";
        EditPrice = 0;
        TestResult = "";
    }

    [RelayCommand]
    private void EditProfile(ProfileItemViewModel? item)
    {
        if (item is null) return;
        var p = item.Profile;
        EditId = p.Id;
        EditName = p.Name;
        EditBaseUrl = p.BaseUrl;
        EditApiKey = "";          // 留空 = 保存时保留原密钥
        EditModel = p.Model;
        EditPrice = p.PricePerMillionTokens;
        TestResult = "";
    }

    /// <summary>本地服务判定:host 为 localhost/127.0.0.1/[::1](大小写不敏感)视为本地,密钥可留空。</summary>
    private static bool IsLocalBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "[::1]" or "::1";
    }

    /// <summary>档案名唯一化:与其他档案(不同 Id)重名时追加 " (2)" " (3)" … 后缀。</summary>
    private static string UniqueProfileName(string name, string selfId)
    {
        bool Taken(string n) => Locator.Profiles.Profiles.Any(p => p.Id != selfId && p.Name == n);
        if (!Taken(name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!Taken(candidate)) return candidate;
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        var isNew = string.IsNullOrEmpty(EditId);
        if (string.IsNullOrWhiteSpace(EditName) ||
            string.IsNullOrWhiteSpace(EditBaseUrl) ||
            string.IsNullOrWhiteSpace(EditModel))
        {
            TestResult = Locator.L10n["Llm.Incomplete"];
            return;
        }
        // 新增态密钥必填,但本地服务(Ollama 等,host 为 localhost/127.0.0.1/[::1])允许留空;
        // 编辑态留空 = 保留原密钥
        if (isNew && string.IsNullOrWhiteSpace(EditApiKey) && !IsLocalBaseUrl(EditBaseUrl))
        {
            TestResult = Locator.L10n["Llm.KeyRequiredRemote"];
            return;
        }
        try
        {
            var id = isNew ? Guid.NewGuid().ToString("N") : EditId;
            // ApiKey 非空 → DPAPI 加密;空且编辑态 → 保留原 EncryptedApiKey
            var encrypted = !string.IsNullOrEmpty(EditApiKey)
                ? SecretProtector.Protect(EditApiKey)
                : Locator.Profiles.Profiles.FirstOrDefault(p => p.Id == id)?.EncryptedApiKey ?? "";
            var name = UniqueProfileName(EditName.Trim(), id);
            Locator.Profiles.Save(new LlmProfile
            {
                Id = id,
                Name = name,
                BaseUrl = EditBaseUrl.Trim(),
                EncryptedApiKey = encrypted,
                Model = EditModel.Trim(),
                PricePerMillionTokens = EditPrice
            });
            EditId = id;            // 首次保存后回写,再次保存为更新而非新增重复档案
            EditApiKey = "";        // 明文用完即清
            Refresh();
        }
        catch (Exception ex)
        {
            TestResult = string.Format(Locator.L10n["Llm.SaveFail"], ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteProfile(ProfileItemViewModel? item)
    {
        if (item is null) return;
        Locator.Profiles.Delete(item.Profile.Id);
        if (EditId == item.Profile.Id) NewProfile();
        Refresh();
    }

    [RelayCommand]
    private void SetActive(ProfileItemViewModel? item)
    {
        if (item is null) return;
        Locator.Profiles.SetActive(item.Profile.Id);
        Refresh();
    }

    /// <summary>用表单当前值(未保存也可测)构造临时档案;表单明文密钥加密后仅存在于该临时对象。</summary>
    private LlmProfile BuildTempProfile()
    {
        var encrypted = !string.IsNullOrEmpty(EditApiKey)
            ? SecretProtector.Protect(EditApiKey)
            : Locator.Profiles.Profiles.FirstOrDefault(p => p.Id == EditId)?.EncryptedApiKey ?? "";
        return new LlmProfile
        {
            Id = string.IsNullOrEmpty(EditId) ? "temp" : EditId,
            Name = EditName,
            BaseUrl = EditBaseUrl.Trim(),
            EncryptedApiKey = encrypted,
            Model = EditModel.Trim(),
            PricePerMillionTokens = EditPrice
        };
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        try
        {
            var models = await Locator.LlmClient.ListModelsAsync(BuildTempProfile());
            TestResult = string.Format(Locator.L10n["Llm.TestOk"], models.Count);
        }
        catch (Exception ex)
        {
            TestResult = string.Format(Locator.L10n["Llm.TestFail"], ex.Message);
        }
        finally { IsTesting = false; }
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        try
        {
            var models = await Locator.LlmClient.ListModelsAsync(BuildTempProfile());
            ModelSuggestions.Clear();
            foreach (var m in models) ModelSuggestions.Add(m);
            TestResult = string.Format(Locator.L10n["Llm.TestOk"], models.Count);
        }
        catch (Exception ex)
        {
            TestResult = string.Format(Locator.L10n["Llm.TestFail"], ex.Message);
        }
        finally { IsTesting = false; }
    }

    // ---------- 触发配置(直读直写 Settings,setter 即 Save,含 NaN 防护) ----------

    public bool LlmThresholdTriggerEnabled
    {
        get => S.LlmThresholdTriggerEnabled;
        set { if (S.LlmThresholdTriggerEnabled == value) return; S.LlmThresholdTriggerEnabled = value; SaveSettings(); OnPropertyChanged(); }
    }

    public double LlmDailyCallCap
    {
        get => S.LlmDailyCallCap;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = (int)Math.Clamp(value, 1, 100);
            if (S.LlmDailyCallCap == v) return;
            S.LlmDailyCallCap = v;
            SaveSettings(); OnPropertyChanged();
        }
    }

    public bool LlmTimerTriggerEnabled
    {
        get => S.LlmTimerTriggerEnabled;
        set { if (S.LlmTimerTriggerEnabled == value) return; S.LlmTimerTriggerEnabled = value; SaveSettings(); OnPropertyChanged(); }
    }

    public double LlmTimerIntervalHours
    {
        get => S.LlmTimerIntervalHours;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = (int)Math.Clamp(value, 1, 168);
            if (S.LlmTimerIntervalHours == v) return;
            S.LlmTimerIntervalHours = v;
            SaveSettings(); OnPropertyChanged();
        }
    }

    public double MonthlyTokenBudget
    {
        get => S.MonthlyTokenBudget;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = (int)Math.Clamp(value, 0, int.MaxValue);
            if (S.MonthlyTokenBudget == v) return;
            S.MonthlyTokenBudget = v;
            SaveSettings(); OnPropertyChanged();
        }
    }

    public string CustomInstructions
    {
        get => S.CustomInstructions;
        set { if (S.CustomInstructions == value) return; S.CustomInstructions = value; SaveSettings(); OnPropertyChanged(); }
    }

    // ---------- 泄漏检测(同样直读直写,数值含 NaN 防护) ----------

    public bool LeakDetectionEnabled
    {
        get => S.LeakDetectionEnabled;
        set { if (S.LeakDetectionEnabled == value) return; S.LeakDetectionEnabled = value; SaveSettings(); OnPropertyChanged(); }
    }

    public double LeakGrowthThresholdMb
    {
        get => S.LeakGrowthThresholdMb;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = (int)Math.Clamp(value, 50, 10000);
            if (S.LeakGrowthThresholdMb == v) return;
            S.LeakGrowthThresholdMb = v;
            SaveSettings(); OnPropertyChanged();
        }
    }

    public double LeakWindowMinutes
    {
        get => S.LeakWindowMinutes;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = (int)Math.Clamp(value, 5, 120);
            if (S.LeakWindowMinutes == v) return;
            S.LeakWindowMinutes = v;
            SaveSettings(); OnPropertyChanged();
        }
    }

    private static void SaveSettings() => Locator.Settings.Save();

    // ---------- 提示词模板 ----------

    [ObservableProperty] private PromptTemplate? _selectedTemplate;
    [ObservableProperty] private string _templateContent = "";
    [ObservableProperty] private string _templateStatus = "";

    partial void OnSelectedTemplateChanged(PromptTemplate? value)
    {
        TemplateContent = value?.Content ?? "";
        TemplateStatus = "";
    }

    private void ReloadTemplates(string? selectId = null)
    {
        Templates.Clear();
        foreach (var t in Locator.Prompts.Templates) Templates.Add(t);
        SelectedTemplate = Templates.FirstOrDefault(t => t.Id == selectId)
                           ?? Templates.FirstOrDefault(t => t.IsDefault)
                           ?? Templates.FirstOrDefault();
    }

    [RelayCommand]
    private void SaveTemplate()
    {
        if (SelectedTemplate is null) return;
        var id = SelectedTemplate.Id;
        Locator.Prompts.Save(SelectedTemplate with { Content = TemplateContent });
        ReloadTemplates(id);
    }

    [RelayCommand]
    private void NewTemplate()
    {
        var t = new PromptTemplate
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Locator.L10n["Llm.CustomTemplate"],
            Content = PromptTemplateService.BuiltinContent
        };
        Locator.Prompts.Save(t);
        ReloadTemplates(t.Id);
    }

    [RelayCommand]
    private void SetDefaultTemplate()
    {
        if (SelectedTemplate is null) return;
        var id = SelectedTemplate.Id;
        Locator.Prompts.Save(SelectedTemplate with { IsDefault = true });
        ReloadTemplates(id);
    }

    [RelayCommand]
    private void RestoreBuiltin()
    {
        Locator.Prompts.RestoreBuiltin();
        ReloadTemplates(PromptTemplateService.BuiltinId);
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (SelectedTemplate is null) return;
        try
        {
            Locator.Prompts.Delete(SelectedTemplate.Id);
            ReloadTemplates();
        }
        catch (InvalidOperationException)
        {
            TemplateStatus = Locator.L10n["Llm.BuiltinNoDelete"];
        }
    }
}
