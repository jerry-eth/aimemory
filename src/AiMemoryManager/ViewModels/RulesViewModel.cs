using CommunityToolkit.Mvvm.ComponentModel;
using AiMemoryManager.Models;
using AiMemoryManager.Services;

namespace AiMemoryManager.ViewModels;

/// <summary>
/// 规则页 VM:直接读写 <see cref="Locator.Settings"/>.Current,任何变更立即 Save()。
/// 页面每次导航重建,构造时读取的就是当前值,无陈旧状态。
/// </summary>
public partial class RulesViewModel : ObservableObject
{
    private static AppSettings S => Locator.Settings.Current;

    public bool ThresholdRuleEnabled
    {
        get => S.ThresholdRuleEnabled;
        set { if (S.ThresholdRuleEnabled == value) return; S.ThresholdRuleEnabled = value; Save(); OnPropertyChanged(); }
    }

    public double ThresholdPercent
    {
        get => S.ThresholdPercent;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = Math.Clamp(value, 40, 95);
            if (S.ThresholdPercent == v) return;
            S.ThresholdPercent = v;
            Save(); OnPropertyChanged();
        }
    }

    public double SustainSeconds
    {
        get => S.SustainSeconds;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = (int)Math.Clamp(value, 10, 300);
            if (S.SustainSeconds == v) return;
            S.SustainSeconds = v;
            Save(); OnPropertyChanged();
        }
    }

    public bool TimerRuleEnabled
    {
        get => S.TimerRuleEnabled;
        set { if (S.TimerRuleEnabled == value) return; S.TimerRuleEnabled = value; Save(); OnPropertyChanged(); }
    }

    public double TimerIntervalMinutes
    {
        get => S.TimerIntervalMinutes;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var v = (int)Math.Clamp(value, 1, 1440);
            if (S.TimerIntervalMinutes == v) return;
            S.TimerIntervalMinutes = v;
            Save(); OnPropertyChanged();
        }
    }

    public bool AutoCleanIncludeL2
    {
        get => S.AutoCleanIncludeL2;
        set { if (S.AutoCleanIncludeL2 == value) return; S.AutoCleanIncludeL2 = value; Save(); OnPropertyChanged(); }
    }

    public bool OnlyWhenNotFullscreen
    {
        get => S.OnlyWhenNotFullscreen;
        set { if (S.OnlyWhenNotFullscreen == value) return; S.OnlyWhenNotFullscreen = value; Save(); OnPropertyChanged(); }
    }

    private static void Save() => Locator.Settings.Save();
}
