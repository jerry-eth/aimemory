using System.IO;
using System.Text.Json;
using AiMemoryManager.Models;

namespace AiMemoryManager.Services;

// ProfileId 放最后并给默认值:M2 旧 jsonl 行无此字段,STJ 反序列化缺失字段时用默认值(null)保兼容
public record TokenUsageRecord(DateTimeOffset Time, string ProfileName, string Model,
    int InputTokens, int OutputTokens, AnalysisTrigger Trigger, string? ProfileId = null);
public record TokenAggregate(int InputTokens, int OutputTokens, int CallCount);

public class TokenStatsService
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;

    public TokenStatsService(string filePath, Func<DateTimeOffset> clock) => (_path, _clock) = (filePath, clock);

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AiMemoryManager", "token-usage.jsonl");

    public void Record(TokenUsageRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(record) + "\n");
    }

    public IReadOnlyList<TokenUsageRecord> LoadAll()
    {
        var list = new List<TokenUsageRecord>();
        try
        {
            if (!File.Exists(_path)) return list;
            foreach (var line in File.ReadAllLines(_path))
            {
                try { if (JsonSerializer.Deserialize<TokenUsageRecord>(line) is { } r) list.Add(r); }
                catch { /* 坏行跳过 */ }
            }
        }
        catch { /* 文件不可读 → 空 */ }
        return list;
    }

    public TokenAggregate AggregateSince(DateTimeOffset since)
    {
        var rs = LoadAll().Where(r => r.Time >= since).ToList();
        return new TokenAggregate(rs.Sum(r => r.InputTokens), rs.Sum(r => r.OutputTokens), rs.Count);
    }

    public TokenAggregate AggregateMonth()
    {
        var first = new DateTimeOffset(_clock().Year, _clock().Month, 1, 0, 0, 0, _clock().Offset);
        return AggregateSince(first);
    }

    public int TodayAutoCallCount()
    {
        var today = _clock().Date;
        return LoadAll().Count(r => r.Time.Date == today && r.Trigger != AnalysisTrigger.Manual);
    }

    public bool IsAutoTriggerAllowed(int monthlyBudget)
    {
        if (monthlyBudget <= 0) return true;
        var m = AggregateMonth();
        return m.InputTokens + m.OutputTokens < monthlyBudget;
    }
}
