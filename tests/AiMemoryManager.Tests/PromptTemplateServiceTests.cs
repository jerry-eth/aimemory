using System.IO;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class PromptTemplateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly PromptTemplateService _svc;

    public PromptTemplateServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "prompts.json");
        _svc = new PromptTemplateService(_path);
    }
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void 首次Load播种出厂模板并设为默认()
    {
        _svc.Load();
        var t = Assert.Single(_svc.Templates);
        Assert.True(t.IsBuiltin);
        Assert.True(t.IsDefault);
        Assert.Contains("{process_list}", t.Content);
        Assert.Contains("{custom_instructions}", t.Content);
        Assert.Contains("{memory_info}", t.Content);
        Assert.Contains("{language}", t.Content);
    }

    [Fact] public void 出厂模板可见可编辑_编辑后持久化()
    {
        _svc.Load();
        var edited = _svc.Templates[0] with { Content = "我的自定义提示词 {process_list}" };
        _svc.Save(edited);
        var svc2 = new PromptTemplateService(_path);
        svc2.Load();
        Assert.Equal("我的自定义提示词 {process_list}", svc2.GetDefault().Content);
    }

    [Fact] public void RestoreBuiltin恢复出厂内容()
    {
        _svc.Load();
        _svc.Save(_svc.Templates[0] with { Content = "改坏了" });
        _svc.RestoreBuiltin();
        Assert.Equal(PromptTemplateService.BuiltinContent, _svc.Templates[0].Content);
    }

    [Fact] public void IsDefault互斥_新默认挤掉旧默认()
    {
        _svc.Load();
        _svc.Save(new PromptTemplate { Id = "t2", Name = "第二", Content = "c2", IsDefault = true });
        Assert.Equal("t2", _svc.GetDefault().Id);
        Assert.False(_svc.Templates.First(t => t.IsBuiltin).IsDefault);
    }

    [Fact] public void 出厂模板拒绝删除()
    {
        _svc.Load();
        Assert.Throws<InvalidOperationException>(() => _svc.Delete(PromptTemplateService.BuiltinId));
    }

    [Fact] public void 自定义模板可删除()
    {
        _svc.Load();
        _svc.Save(new PromptTemplate { Id = "t2", Name = "第二", Content = "c2" });
        _svc.Delete("t2");
        Assert.Single(_svc.Templates);
    }
}
