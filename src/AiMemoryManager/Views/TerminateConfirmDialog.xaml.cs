using System.Windows;
using AiMemoryManager.Services;
using Wpf.Ui.Controls;

namespace AiMemoryManager.Views;

/// <summary>确认对话框的一行:进程信息 + 未保存高风险标记(由调用方逐个查 Unsaved 后传入)。</summary>
public record TerminateConfirmItem(int Pid, string Name, string? Path, long MemoryBytes, bool IsHighRisk)
{
    public string MemoryText => $"{MemoryBytes / (1 << 20)} MB";
}

/// <summary>
/// FR-2.3/2.6:L3 结束进程确认对话框。清单 + 汇总 + 高风险警告,确认后才允许终止。
/// 纯展示层:数据全部经构造函数传入,结果用 DialogResult(true=确认) 返回。
/// </summary>
public partial class TerminateConfirmDialog : FluentWindow
{
    /// <summary>确认后要终止的 PID 列表(与传入清单一致)。</summary>
    public IReadOnlyList<int> SelectedPids { get; }

    public TerminateConfirmDialog(IReadOnlyList<TerminateConfirmItem> items)
    {
        InitializeComponent();
        SelectedPids = items.Select(i => i.Pid).ToList();
        ItemList.ItemsSource = items;
        SummaryText.Text = string.Format(
            Locator.L10n["L3.ConfirmSummary"], items.Count, items.Sum(i => i.MemoryBytes) / (1 << 20));
        if (items.Any(i => i.IsHighRisk))
        {
            WarningText.Text = Locator.L10n["L3.HighRiskWarning"];
            WarningText.Visibility = Visibility.Visible;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
