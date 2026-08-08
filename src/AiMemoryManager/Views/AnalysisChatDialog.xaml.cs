using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using AiMemoryManager.Models;
using AiMemoryManager.Services;
using AiMemoryManager.ViewModels;
using Wpf.Ui.Controls;

namespace AiMemoryManager.Views;

public partial class AnalysisChatDialog : FluentWindow
{
    private readonly ObservableCollection<AnalysisChatMessage> _messages = new();
    private AnalysisActionPlan? _pendingPlan;
    private bool _busy;

    public AnalysisChatDialog()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _messages;
        AddMessage("assistant", Locator.L10n["Analysis.ChatNoPlan"]);
        if (SmartAnalysisViewModel.Instance.Report is { } report)
            AddMessage("assistant", $"{report.Summary} {report.Recommendations.FirstOrDefault() ?? ""}".Trim());
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(InputBox.Text)) return;
        var text = InputBox.Text.Trim();
        InputBox.Clear();
        AddMessage("user", text);
        _busy = true;
        try
        {
            var response = await Locator.AnalysisChat.ChatAsync(
                SmartAnalysisViewModel.Instance.Report, _messages.ToList(), text);
            AddMessage("assistant", response.Answer);
            _pendingPlan = response.Plan;
            if (_pendingPlan is { IsExecutable: true })
            {
                PlanText.Text = DescribePlan(_pendingPlan);
                PlanPanel.Visibility = Visibility.Visible;
            }
            else
            {
                PlanPanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            AddMessage("assistant", ex.Message);
        }
        finally { _busy = false; }
    }

    private async void OnExecutePlan(object sender, RoutedEventArgs e)
    {
        if (_pendingPlan is not { IsExecutable: true } plan) return;
        try
        {
            IReadOnlyCollection<int> pids = Array.Empty<int>();
            if (plan.Operation == "terminate_processes")
            {
                var targets = Locator.AnalysisActions.ResolveTerminateTargets(plan);
                if (targets.Count == 0)
                {
                    AddMessage("assistant", Locator.L10n["Analysis.NoneKillable"]);
                    PlanPanel.Visibility = Visibility.Collapsed;
                    return;
                }
                var rows = targets.Select(t => new TerminateConfirmItem(t.Pid, t.Name, t.Path,
                    t.WorkingSetBytes, Locator.Unsaved.HasUnsavedSigns(t.Pid))).ToList();
                var dialog = new TerminateConfirmDialog(rows) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                pids = dialog.SelectedPids;
            }
            else
            {
                var title = Locator.L10n["Analysis.ChatTitle"];
                var message = DescribePlan(plan) + "\n\n" + Locator.L10n["Analysis.ChatConfirm"];
                var dialog = new SlimConfirmDialog(title, message, Locator.L10n["Analysis.ChatExecute"]) { Owner = this };
                if (dialog.ShowDialog() != true) return;
            }

            var result = await Locator.AnalysisActions.ExecuteAsync(plan, pids);
            AddMessage("assistant", string.Format(Locator.L10n["Analysis.ChatExecuted"],
                result.FreedBytes / (1 << 20), result.ProcessCount, result.FailedCount));
            _pendingPlan = null;
            PlanPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { AddMessage("assistant", ex.Message); }
    }

    private void AddMessage(string role, string content)
    {
        _messages.Add(new AnalysisChatMessage(role, content, DateTimeOffset.Now));
    }

    private static string DescribePlan(AnalysisActionPlan plan)
    {
        var operation = plan.Operation switch
        {
            "clean_working_sets" => "清理进程工作集",
            "purge_standby" => "清理待机列表",
            "terminate_processes" => "结束指定进程",
            _ => "无操作"
        };
        var targets = plan.Targets.Count == 0 ? "全部可清理目标" : string.Join(", ", plan.Targets);
        return $"{operation}：{targets}\n理由：{plan.Reason}\n风险：{plan.Risk}";
    }
}


