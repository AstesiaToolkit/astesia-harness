using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>设置页文本输入失焦：立即保存（无需等待防抖，也无需点「保存设置」）。</summary>
    private void OnSettingsFieldLostFocus(object sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.CommitPendingSettings();
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

    /// <summary>日志区 Ctrl+C：复制选中的日志行；无选中时不拦截（保持默认行为）。</summary>
    private void OnLogPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (DataContext is not MainViewModel vm) return;
        if (LogList.SelectedItems.Count == 0) return;
        e.Handled = true;
        vm.CopySelectedLogs(LogList.SelectedItems.Cast<LogEntry>().ToList());
    }

    // ── 日志区左键拖动多选 ──────────────────────────────────────────
    // WPF ListBox 的 Extended 只支持 Shift/Ctrl+点击，不原生支持鼠标拖动拉选。
    // 此处按下记录起始项，移动超阈值后按「起始项→当前项」的连续区间重建选中。

    private bool _dragSelecting;
    private int _dragStartIndex = -1;
    private int _dragPrevLow = -1;
    private int _dragPrevHigh = -1;

    private void OnLogMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragSelecting = false;
        _dragStartIndex = -1;
        // 按住 Ctrl/Shift 时交给 ListBox 原生多选，避免冲突
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;
        _dragStartIndex = IndexOfLogItem(e.GetPosition(LogList));
    }

    private void OnLogMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStartIndex < 0) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _dragSelecting = false;
            _dragStartIndex = -1;
            _dragPrevLow = _dragPrevHigh = -1;
            return;
        }

        var idx = IndexOfLogItem(e.GetPosition(LogList));
        if (!_dragSelecting)
        {
            // 未移动超过一行时不视为拖拽，保留单击/轻点行为
            if (idx < 0 || idx == _dragStartIndex) return;
            _dragSelecting = true;
        }
        if (idx < 0) return;

        var low = Math.Min(_dragStartIndex, idx);
        var high = Math.Max(_dragStartIndex, idx);
        if (low == _dragPrevLow && high == _dragPrevHigh) return; // 范围未变化，避免重复重建
        SelectLogRange(low, high, _dragPrevLow, _dragPrevHigh);
        _dragPrevLow = low;
        _dragPrevHigh = high;
    }

    /// <summary>把日志选中区间重建为 [low, high]（增量更新，取消旧范围、选中新范围）。</summary>
    private void SelectLogRange(int low, int high, int prevLow, int prevHigh)
    {
        var count = LogList.Items.Count;
        if (count == 0) return;
        low = Math.Max(0, low);
        high = Math.Min(count - 1, high);

        // 先取消不再落在新范围内的旧选中项
        if (prevLow >= 0)
        {
            for (var i = prevLow; i <= prevHigh && i < count; i++)
            {
                if (i < low || i > high) SetLogSelected(i, false);
            }
        }
        // 再选中新增范围内的项
        for (var i = low; i <= high; i++)
        {
            if (prevLow < 0 || i < prevLow || i > prevHigh) SetLogSelected(i, true);
        }
    }

    private void SetLogSelected(int index, bool selected)
    {
        if (index < 0 || index >= LogList.Items.Count) return;
        if (LogList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) return;
        if (item.IsSelected != selected) item.IsSelected = selected;
    }

    /// <summary>返回鼠标位置所在日志行的索引；不在日志行上（空白/滚动条等）返回 -1。</summary>
    private int IndexOfLogItem(System.Windows.Point pos)
    {
        if (LogList.InputHitTest(pos) is not DependencyObject hit) return -1;
        var item = FindAncestor<ListBoxItem>(hit);
        return item is null ? -1 : LogList.ItemContainerGenerator.IndexFromContainer(item);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
