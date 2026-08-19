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
    }

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
        if (vm.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
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
