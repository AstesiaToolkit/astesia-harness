using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using AstesiaHarness.ViewModels;

namespace AstesiaHarness;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnWindowClosing;
        StateChanged += OnWindowStateChanged;
        // 任务栏/Alt+Tab 标题带版本号
        Title = $"AstesiaHarness v{AstesiaHarness.Services.UpdateService.CurrentVersion} — DeepSeek Harness 快速启动器";
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "\u2750" : "\u25A1";
    }

    private void OnMinimizeButton(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreButton(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseButton(object sender, RoutedEventArgs e) => Close();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // DataContext 在构造后由 App 注入；此处建立日志集合订阅。
        if (e.NewValue is MainViewModel vm)
        {
            vm.Log.CollectionChanged += OnLogCollectionChanged;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        if (vm is null || vm.IsExiting) return;
        // T1：按设置/对话框决定 退出程序 / 最小化到托盘 / 取消
        switch (vm.HandleWindowClosing())
        {
            case MainViewModel.WindowCloseResult.Cancel:
                e.Cancel = true;
                break;
            case MainViewModel.WindowCloseResult.Minimize:
                e.Cancel = true;
                Hide();
                break;
            case MainViewModel.WindowCloseResult.Proceed:
                // 允许关闭；退出流程（含服务确认）已在 VM 中处理
                break;
        }
    }

    private void OnLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        var vm = DataContext as MainViewModel;
        if (vm is null || !vm.AutoScroll) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (LogList.Items.Count == 0) return;
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        });
    }
}
