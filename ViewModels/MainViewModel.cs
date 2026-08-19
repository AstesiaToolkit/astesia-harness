using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AstesiaHarness.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace AstesiaHarness.ViewModels;

/// <summary>
/// 主视图模型：状态展示、操作命令、日志缓冲、设置项，与托盘共享同一实例。
/// 服务层事件来自线程池线程，统一编组回 UI 线程后更新。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxLogLines = 5000;

    private readonly SettingsStore _settings;
    private readonly DshProcessManager _manager;
    private readonly SynchronizationContext _ui;
    private readonly DispatcherTimer _uptimeTimer;

    private DateTime _runningSince;
    private bool _isExiting;
    private string? _url;
    private int? _pid;
    private string _stateText = "已停止";
    private Brush _stateBrush = GrayBrush;
    private string _metaText = "";
    private string _messageText = "";
    private Brush _messageBrush = InfoBrush;

    private string _repoPath;
    private string _portText;
    private string _host;
    private string _extraArgs;
    private bool _autoOpenBrowser;
    private bool _minimizeToTrayOnClose;
    private bool _startWithWindows;

    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

    public MainViewModel(SettingsStore settings, DshProcessManager manager)
    {
        _settings = settings;
        _manager = manager;
        _ui = SynchronizationContext.Current ?? new DispatcherSynchronizationContext();

        var s = settings.Current;
        _repoPath = s.RepoPath;
        _portText = s.Port.ToString();
        _host = s.Host;
        _extraArgs = s.ExtraArgs;
        _autoOpenBrowser = s.AutoOpenBrowser;
        _minimizeToTrayOnClose = s.MinimizeToTrayOnClose;
        _startWithWindows = s.StartWithWindows;

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UpdateMeta();

        // ── 命令 ────────────────────────────────────────────────────
        StartCommand = new RelayCommand(
            () => RunSafely(async () =>
            {
                await _manager.StartAsync();
                _pid = _manager.ProcessId;
                UpdateMeta();
            }),
            () => _manager.State is ServerState.Stopped or ServerState.Failed);
        StopCommand = new RelayCommand(
            () => RunSafely(async () =>
            {
                await _manager.StopAsync();
                _pid = null;
                UpdateMeta();
            }),
            () => _manager.State is ServerState.Starting or ServerState.Running);
        RestartCommand = new RelayCommand(
            () => RunSafely(async () =>
            {
                await _manager.RestartAsync();
                _pid = _manager.ProcessId;
                UpdateMeta();
            }),
            () => _manager.State is ServerState.Running);
        // 只要 DSH 在运行即可打开/复制 URL（不依赖就绪行是否已解析，修复自动打开后按钮置灰的问题）
        OpenBrowserCommand = new RelayCommand(OpenBrowser, () => _manager.State is ServerState.Running);
        CopyUrlCommand = new RelayCommand(() =>
        {
            var target = ResolveUrl();
            if (string.IsNullOrEmpty(target)) return;
            Clipboard.SetText(target);
            ShowMessage("URL 已复制到剪贴板。");
        }, () => _manager.State is ServerState.Running);

        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        BrowseRepoCommand = new RelayCommand(BrowseRepo);
        OpenDataDirCommand = new RelayCommand(OpenDataDir);
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        CopyLogCommand = new RelayCommand(CopyLog);

        // ── 服务层事件 ──────────────────────────────────────────────
        _manager.StateChanged += OnStateChanged;
        _manager.LogLine += OnLogLine;
        _manager.Ready += OnReady;
        _manager.Error += OnManagerError;

        RefreshCommandStates();
        UpdateMeta();
    }

    /// <summary>托盘图标（由 App 注入，状态变化时刷新 tooltip）。</summary>
    public Tray.TrayIcon? Tray { get; set; }

    /// <summary>主窗口（由 App 注入）。</summary>
    public Window? Window { get; set; }

    /// <summary>正在退出（托盘"退出"流程），此时关闭窗口不再拦截为最小化。</summary>
    public bool IsExiting
    {
        get => _isExiting;
        set { _isExiting = value; OnPropertyChanged(); }
    }

    // ── 展示属性 ────────────────────────────────────────────────────

    public ObservableCollection<LogEntry> Log { get; } = new();

    public bool AutoScroll { get; set; } = true;

    public string StateText => _stateText;
    public Brush StateBrush => _stateBrush;
    public string MetaText => _metaText;
    public string MessageText => _messageText;
    public Brush MessageBrush => _messageBrush;

    public string? Url
    {
        get => _url;
        private set { _url = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUrl)); }
    }

    public bool HasUrl => _url is not null;

    // ── 设置属性（双向绑定） ────────────────────────────────────────

    public string RepoPath { get => _repoPath; set { _repoPath = value; OnPropertyChanged(); } }
    public string PortText { get => _portText; set { _portText = value; OnPropertyChanged(); } }
    public string Host { get => _host; set { _host = value; OnPropertyChanged(); } }
    public string ExtraArgs { get => _extraArgs; set { _extraArgs = value; OnPropertyChanged(); } }
    public bool AutoOpenBrowser { get => _autoOpenBrowser; set { _autoOpenBrowser = value; OnPropertyChanged(); } }
    public bool MinimizeToTrayOnClose { get => _minimizeToTrayOnClose; set { _minimizeToTrayOnClose = value; OnPropertyChanged(); } }
    public bool StartWithWindows { get => _startWithWindows; set { _startWithWindows = value; OnPropertyChanged(); } }

    public string[] HostSuggestions { get; } = { "127.0.0.1", "localhost" };

    // ── 命令 ────────────────────────────────────────────────────────

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand OpenBrowserCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand BrowseRepoCommand { get; }
    public RelayCommand OpenDataDirCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CopyLogCommand { get; }

    // ── 托盘菜单入口 ────────────────────────────────────────────────

    public void RequestStart() => StartCommand.Execute(null);
    public void RequestStop() => StopCommand.Execute(null);
    public void RequestRestart() => RestartCommand.Execute(null);
    public void RequestOpenBrowser() => OpenBrowserCommand.Execute(null);
    public void RequestShowWindow() => ShowWindow();
    public void RequestExit() => ExitFromTray();

    // ── 事件处理（编组到 UI 线程） ─────────────────────────────────

    private void OnStateChanged(ServerState state) =>
        _ui.Post(_ =>
        {
            if (_isExiting) return;
            switch (state)
            {
                case ServerState.Stopped:
                    _stateText = "已停止";
                    _stateBrush = GrayBrush;
                    _uptimeTimer.Stop();
                    Url = null;
                    _pid = null;
                    break;
                case ServerState.Starting:
                    _stateText = "启动中…";
                    _stateBrush = OrangeBrush;
                    break;
                case ServerState.Running:
                    _stateText = "运行中";
                    _stateBrush = GreenBrush;
                    _runningSince = DateTime.Now;
                    _uptimeTimer.Start();
                    break;
                case ServerState.Failed:
                    _stateText = "启动失败";
                    _stateBrush = RedBrush;
                    _uptimeTimer.Stop();
                    Url = null;
                    break;
            }
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateBrush));
            RefreshCommandStates();
            UpdateMeta();
            UpdateTrayTooltip();
        }, null);

    private void OnLogLine(string text, bool isError) =>
        _ui.Post(_ =>
        {
            if (_isExiting) return;
            AppendLog(text, isError);
        }, null);

    private void OnReady(string url) =>
        _ui.Post(_ =>
        {
            if (_isExiting) return;
            Url = url;
            UpdateMeta();
            UpdateTrayTooltip();
            if (_autoOpenBrowser)
            {
                OpenBrowser();
                ShowMessage("服务已就绪，已打开浏览器。");
            }
            else
            {
                ShowMessage("服务已就绪。");
            }
        }, null);

    private void OnManagerError(string message) =>
        _ui.Post(_ =>
        {
            if (_isExiting) return;
            ShowMessage(message, isError: true);
            UpdateTrayTooltip();
        }, null);

    // ── 动作 ────────────────────────────────────────────────────────

    private async void RunSafely(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowMessage($"操作失败：{ex.Message}", isError: true);
        }
    }

    private async void OpenBrowser()
    {
        var target = ResolveUrl();
        if (string.IsNullOrEmpty(target)) return;
        try
        {
            // UIA 探测（含最小化窗口恢复等待）放后台线程，避免阻塞 UI
            await Task.Run(() => BrowserOpener.OpenOrFocus(target));
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
    }

    /// <summary>
    /// 当前可访问 URL：优先用就绪行解析到的 URL，否则按配置的 host:port 构造（Running 时恒有值）。
    /// </summary>
    private string ResolveUrl() => _url ?? $"http://{_settings.Current.Host}:{_settings.Current.Port}";

    private void SaveSettings()
    {
        if (!int.TryParse(_portText.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowMessage("端口必须是 1–65535 的整数。", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_repoPath))
        {
            ShowMessage("仓库路径不能为空。", isError: true);
            return;
        }

        var s = _settings.Current;
        s.RepoPath = _repoPath.Trim();
        s.Port = port;
        s.Host = string.IsNullOrWhiteSpace(_host) ? "127.0.0.1" : _host.Trim();
        s.ExtraArgs = _extraArgs.Trim();
        s.AutoOpenBrowser = _autoOpenBrowser;
        s.MinimizeToTrayOnClose = _minimizeToTrayOnClose;
        s.StartWithWindows = _startWithWindows;

        _settings.Save();
        _portText = s.Port.ToString();
        OnPropertyChanged(nameof(PortText));

        var running = _manager.State is ServerState.Starting or ServerState.Running;
        ShowMessage(running ? "设置已保存；运行中的参数将在下次重启后生效。" : "设置已保存。");
    }

    private void ResetSettings()
    {
        _settings.ResetToDefaults();
        var s = _settings.Current;
        RepoPath = s.RepoPath;
        PortText = s.Port.ToString();
        Host = s.Host;
        ExtraArgs = s.ExtraArgs;
        AutoOpenBrowser = s.AutoOpenBrowser;
        MinimizeToTrayOnClose = s.MinimizeToTrayOnClose;
        StartWithWindows = s.StartWithWindows;
        ShowMessage("已恢复默认值（未保存，点击「保存设置」生效）。");
    }

    private void BrowseRepo()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 DeepSeek Harness 仓库目录",
                InitialDirectory = Directory.Exists(_repoPath) ? _repoPath : Environment.GetFolderPath(Environment.SpecialFolder.MyComputer),
            };
            if (dialog.ShowDialog(Window) == true) RepoPath = dialog.FolderName;
        }
        catch (Exception ex)
        {
            ShowMessage($"无法打开目录选择器：{ex.Message}", isError: true);
        }
    }

    private void OpenDataDir()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", SettingsStore.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage($"无法打开数据目录：{ex.Message}", isError: true);
        }
    }

    private void CopyLog()
    {
        var text = string.Join(Environment.NewLine, Log.Select(l => $"[{l.TimeText}] {l.Text}"));
        if (text.Length == 0) return;
        Clipboard.SetText(text);
        ShowMessage("日志已复制到剪贴板。");
    }

    private void AppendLog(string text, bool isError)
    {
        Log.Add(new LogEntry(text, isError));
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    private void ShowMessage(string message, bool isError = false)
    {
        _messageText = message;
        _messageBrush = isError ? ErrorBrush : InfoBrush;
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(MessageBrush));
    }

    private void UpdateMeta()
    {
        var parts = new List<string>();
        if (_pid is not null) parts.Add($"PID {_pid}");
        if (_manager.State == ServerState.Running)
        {
            var elapsed = DateTime.Now - _runningSince;
            parts.Add($"已运行 {elapsed:hh\\:mm\\:ss}");
        }
        parts.Add($"端口 {_portText}");
        _metaText = string.Join(" · ", parts);
        OnPropertyChanged(nameof(MetaText));
    }

    private void RefreshCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        OpenBrowserCommand.RaiseCanExecuteChanged();
        CopyUrlCommand.RaiseCanExecuteChanged();
    }

    private void UpdateTrayTooltip()
    {
        var state = _manager.State;
        var text = state switch
        {
            ServerState.Stopped => "AstesiaHarness — 已停止",
            ServerState.Starting => "AstesiaHarness — 启动中…",
            ServerState.Running => _url is null ? "AstesiaHarness — 运行中" : $"AstesiaHarness — 运行中 · {_url}",
            ServerState.Failed => "AstesiaHarness — 启动失败",
            _ => "AstesiaHarness",
        };
        Tray?.UpdateStatus(text);
    }

    private void ShowWindow()
    {
        var w = Window;
        if (w is null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
    }

    private void ExitFromTray()
    {
        var state = _manager.State;
        if (state is ServerState.Starting or ServerState.Running)
        {
            var choice = MessageBox.Show(
                "DSH Web 服务正在运行。\n\n" +
                "是(Y)：停止服务并退出\n" +
                "否(N)：保持服务运行，仅退出启动器\n" +
                "取消：返回",
                "退出 AstesiaHarness",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            switch (choice)
            {
                case MessageBoxResult.Yes:
                    IsExiting = true;
                    RunSafely(async () =>
                    {
                        await _manager.StopAsync();
                        Application.Current.Shutdown();
                    });
                    return;
                case MessageBoxResult.No:
                    _manager.DetachAndRelease();
                    IsExiting = true;
                    Application.Current.Shutdown();
                    return;
                default:
                    return;
            }
        }
        IsExiting = true;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _manager.StateChanged -= OnStateChanged;
        _manager.LogLine -= OnLogLine;
        _manager.Ready -= OnReady;
        _manager.Error -= OnManagerError;
        _uptimeTimer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
