using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

public class PromptTemplateService
{
    public const string BuiltinId = "builtin-default";

    public static string BuiltinContent { get; } = """
        你是 Windows 内存管理助手。当前系统内存状况:{memory_info}。
        以下是内存占用最高的进程列表(JSON 数组,字段:name=进程名,memoryMB=内存MB,path=路径):
        {process_list}

        用户附加要求:{custom_instructions}

        请分析并给出每个值得处理的进程的建议,只输出 JSON,格式:
        {"suggestions":[{"process":"进程名","action":"compress|terminate|keep","reason":"一句话理由","risk":"low|medium|high"}]}
        规则:
        - compress=可回收其工作集(内存高、非前台关键应用);terminate=建议用户关闭(确认无用的应用);keep=保留
        - 拿不准的一律 keep;系统关键进程不要出现在建议中
        - reason 用{language}书写,不超过 30 字
        """;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _path;
    private readonly List<PromptTemplate> _templates = new();

    public IReadOnlyList<PromptTemplate> Templates => _templates;

    public PromptTemplateService(string filePath) => _path = filePath;

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "prompts.json");

    public void Load()
    {
        _templates.Clear();
        try
        {
            if (File.Exists(_path))
                _templates.AddRange(JsonSerializer.Deserialize<List<PromptTemplate>>(File.ReadAllText(_path)) ?? new());
        }
        catch { _templates.Clear(); }
        if (_templates.All(t => t.Id != BuiltinId))
            _templates.Insert(0, new PromptTemplate
            { Id = BuiltinId, Name = "默认模板", Content = BuiltinContent, IsDefault = true, IsBuiltin = true });
        if (_templates.All(t => !t.IsDefault))
            _templates[0] = _templates[0] with { IsDefault = true };
        Persist();
    }

    public PromptTemplate GetDefault() => _templates.First(t => t.IsDefault);

    public void Save(PromptTemplate template)
    {
        var i = _templates.FindIndex(t => t.Id == template.Id);
        if (i >= 0) _templates[i] = template; else _templates.Add(template);
        if (template.IsDefault)
            for (int k = 0; k < _templates.Count; k++)
                if (_templates[k].Id != template.Id && _templates[k].IsDefault)
                    _templates[k] = _templates[k] with { IsDefault = false };
        Persist();
    }

    public void Delete(string id)
    {
        if (id == BuiltinId) throw new InvalidOperationException("出厂模板不可删除");
        _templates.RemoveAll(t => t.Id == id);
        if (_templates.All(t => !t.IsDefault))
            _templates[0] = _templates[0] with { IsDefault = true };
        Persist();
    }

    public void RestoreBuiltin()
    {
        var i = _templates.FindIndex(t => t.Id == BuiltinId);
        _templates[i] = _templates[i] with { Content = BuiltinContent };
        Persist();
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_templates, JsonOpts));
    }
}
