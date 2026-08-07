using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

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
    public ObservableCollection<PromptTemplate> Templates { get; } = new();

    // 编辑表单(新增/编辑共用)
    [ObservableProperty] private string _editId = "";
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editBaseUrl = "https://api.deepseek.com/v1";
    [ObservableProperty] private string _editApiKey = "";        // 明文仅存在于表单,保存即加密
    [ObservableProperty] private string _editModel = "";
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
        Refresh();
        ReloadTemplates();
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

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(EditName) ||
            string.IsNullOrWhiteSpace(EditBaseUrl) ||
            string.IsNullOrWhiteSpace(EditModel))
        {
            TestResult = Locator.L10n["Llm.Incomplete"];
            return;
        }
        var id = string.IsNullOrEmpty(EditId) ? Guid.NewGuid().ToString("N") : EditId;
        // ApiKey 非空 → DPAPI 加密;空且编辑态 → 保留原 EncryptedApiKey
        var encrypted = !string.IsNullOrEmpty(EditApiKey)
            ? SecretProtector.Protect(EditApiKey)
            : Locator.Profiles.Profiles.FirstOrDefault(p => p.Id == id)?.EncryptedApiKey ?? "";
        Locator.Profiles.Save(new LlmProfile
        {
            Id = id,
            Name = EditName.Trim(),
            BaseUrl = EditBaseUrl.Trim(),
            EncryptedApiKey = encrypted,
            Model = EditModel.Trim(),
            PricePerMillionTokens = EditPrice
        });
        EditApiKey = "";          // 明文用完即清
        Refresh();
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
