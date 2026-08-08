using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

/// <summary>按档案聚合行:费用为档案单价 × 该档案全部记录 token 数(无档案或单价 0 → "-")。</summary>
public sealed record ProfileAggRow(string Profile, string InputText, string OutputText, string CallsText, string CostText);

/// <summary>最近调用行:时间预格式化为本地时间,触发方式已本地化。</summary>
public sealed record RecentCallRow(string TimeText, string Profile, string Model,
    string InputText, string OutputText, string TriggerText);

/// <summary>
/// Token 统计页 VM:日/周/月聚合、费用估算(按档案单价 × 月累计)、预算告警、
/// 按档案聚合与最近 50 条调用。无事件订阅,无需 Dispose;导航重建页面时构造即刷新。
/// </summary>
public partial class TokenStatsViewModel : ObservableObject
{
    [ObservableProperty] private string _todayText = "-";
    [ObservableProperty] private string _weekText = "-";
    [ObservableProperty] private string _monthText = "-";
    [ObservableProperty] private string _costText = "-";
    [ObservableProperty] private bool _budgetHit;

    public ObservableCollection<ProfileAggRow> ByProfile { get; } = new();
    public ObservableCollection<RecentCallRow> Recent { get; } = new();

    public TokenStatsViewModel() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var stats = Locator.TokenStats;
        var now = DateTimeOffset.Now;
        TodayText = Format(stats.AggregateSince(now.Date));
        WeekText = Format(stats.AggregateSince(now.Date.AddDays(-(int)now.DayOfWeek)));
        MonthText = Format(stats.AggregateMonth());

        var all = stats.LoadAll();
        var profiles = Locator.Profiles.Profiles;
        var byId = profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        // 同名档案(历史遗留)取第一个,不崩
        var byName = profiles.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());

        // 费用/分组优先按 ProfileId 匹配档案;ProfileId 为 null(M2 旧记录)回退按档案名 —— 旧数据兼容
        string GroupKey(TokenUsageRecord r) => r.ProfileId ?? r.ProfileName;
        double PriceOf(TokenUsageRecord r)
        {
            if (r.ProfileId != null && byId.TryGetValue(r.ProfileId, out var pi))
                return pi.PricePerMillionTokens;
            return byName.TryGetValue(r.ProfileName, out var pn) ? pn.PricePerMillionTokens : 0;
        }
        string DisplayName(IGrouping<string, TokenUsageRecord> g)
        {
            var first = g.First();
            return first.ProfileId != null && byId.TryGetValue(first.ProfileId, out var p)
                ? p.Name : first.ProfileName;
        }

        // 费用:各档案单价 × 该档案本月累计(缺价按 0)
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        double monthCost = 0;
        foreach (var g in all.Where(r => r.Time >= monthStart).GroupBy(GroupKey))
            monthCost += PriceOf(g.First()) * (g.Sum(r => r.InputTokens) + g.Sum(r => r.OutputTokens)) / 1_000_000d;
        CostText = monthCost > 0 ? monthCost.ToString("$0.0000") : "-";

        var budget = Locator.Settings.Current.MonthlyTokenBudget;
        BudgetHit = budget > 0 && !stats.IsAutoTriggerAllowed(budget);

        ByProfile.Clear();
        foreach (var g in all.GroupBy(GroupKey)
                             .OrderByDescending(g => g.Sum(r => r.InputTokens + r.OutputTokens)))
        {
            var cost = PriceOf(g.First()) * (g.Sum(r => r.InputTokens) + g.Sum(r => r.OutputTokens)) / 1_000_000d;
            ByProfile.Add(new ProfileAggRow(DisplayName(g),
                g.Sum(r => r.InputTokens).ToString("N0"),
                g.Sum(r => r.OutputTokens).ToString("N0"),
                g.Count().ToString("N0"),
                cost > 0 ? cost.ToString("$0.0000") : "-"));
        }

        Recent.Clear();
        foreach (var r in all.OrderByDescending(r => r.Time).Take(50))
            Recent.Add(new RecentCallRow(r.Time.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                r.ProfileName, r.Model,
                r.InputTokens.ToString("N0"), r.OutputTokens.ToString("N0"),
                Locator.L10n["Tokens.Trigger." + r.Trigger]));
    }

    private static string Format(TokenAggregate a)
    {
        var l = Locator.L10n;
        return $"{l["Tokens.Input"]} {a.InputTokens:N0} · {l["Tokens.Output"]} {a.OutputTokens:N0} · {l["Tokens.Calls"]} {a.CallCount:N0}";
    }
}
