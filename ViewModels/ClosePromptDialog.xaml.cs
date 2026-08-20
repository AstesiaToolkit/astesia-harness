using System.Windows;
using AstesiaHarness.Services;

namespace AstesiaHarness.ViewModels;

/// <summary>
/// 关闭主界面时的选择对话框（T1）：互斥单选「退出程序 / 最小化到托盘」、
/// 独立复选「不再提示」，按钮「取消 / 确定」。
/// </summary>
public partial class ClosePromptDialog : Window
{
    /// <summary>用户最终选择的行为（仅 DialogResult == true 时有意义）。</summary>
    public CloseAction SelectedAction { get; private set; }

    /// <summary>用户是否勾选「不再提示」（仅 DialogResult == true 时有意义）。</summary>
    public bool DontAskAgain { get; private set; }

    public ClosePromptDialog(CloseAction defaultAction)
    {
        InitializeComponent();
        MinimizeRadio.IsChecked = defaultAction == CloseAction.MinimizeToTray;
        ExitRadio.IsChecked = defaultAction == CloseAction.Exit;
        OkButton.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        SelectedAction = ExitRadio.IsChecked == true ? CloseAction.Exit : CloseAction.MinimizeToTray;
        DontAskAgain = DontAskCheck.IsChecked == true;
        DialogResult = true;
    }
}
