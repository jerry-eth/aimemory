using System.Windows;
using Wpf.Ui.Controls;

namespace AiMemoryManager.Views;

/// <summary>
/// FR-12 通用确认对话框(照 Task 7 先例):标题/正文/确认按钮文案经构造函数传入,
/// DialogResult(true=确认) 返回。用于删除入回收站与跨盘迁移两个确认点。
/// </summary>
public partial class SlimConfirmDialog : FluentWindow
{
    public SlimConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
