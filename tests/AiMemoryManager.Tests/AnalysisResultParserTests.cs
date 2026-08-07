using AiMemoryManager.Services;

namespace AiMemoryManager.Tests;

public class AnalysisResultParserTests
{
    [Fact] public void 解析标准输出()
    {
        var r = AnalysisResultParser.Parse("""
            {"suggestions":[{"process":"chrome","action":"compress","reason":"占用高","risk":"low"},
                            {"process":"game","action":"terminate","reason":"已退出前台","risk":"medium"}]}
            """);
        Assert.Equal(2, r.Count);
        Assert.Equal("chrome", r[0].ProcessName);
        Assert.Equal("compress", r[0].Action);
        Assert.Equal("medium", r[1].Risk);
    }

    [Fact] public void 容忍markdown代码块包裹()
    {
        var r = AnalysisResultParser.Parse("好的,分析如下:\n```json\n{\"suggestions\":[{\"process\":\"a\",\"action\":\"keep\",\"reason\":\"r\",\"risk\":\"low\"}]}\n```\n完毕");
        Assert.Single(r);
        Assert.Equal("a", r[0].ProcessName);
    }

    [Fact] public void 坏输出返回空列表不抛异常()
    {
        Assert.Empty(AnalysisResultParser.Parse("这不是 JSON"));
        Assert.Empty(AnalysisResultParser.Parse("{\"别的\":1}"));
        Assert.Empty(AnalysisResultParser.Parse(""));
    }

    [Fact] public void 非法action与risk归一化()
    {
        var r = AnalysisResultParser.Parse("""{"suggestions":[{"process":"a","action":"KILL","reason":"r","risk":"超高"}]}""");
        Assert.Single(r);
        Assert.Equal("keep", r[0].Action);   // 未知动作归 keep(最安全)
        Assert.Equal("medium", r[0].Risk);   // 未知风险归 medium
    }

    [Fact] public void 缺字段条目被跳过()
    {
        var r = AnalysisResultParser.Parse("""{"suggestions":[{"action":"compress"},{"process":"b","action":"compress","reason":"ok","risk":"low"}]}""");
        Assert.Single(r);
        Assert.Equal("b", r[0].ProcessName);
    }
}
