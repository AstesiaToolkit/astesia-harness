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

    /// <summary>文本类设置自动保存的防抖间隔（停止输入后延迟落盘）。</summary>
    private static readonly TimeSpan SettingsAutoSaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly SettingsStore _settings;
    private readonly DshProcessManager _manager;
    private readonly SynchronizationContext _ui;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _settingsDebounce;
    private bool _suppressAutoSave;

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
    private CloseAction _closeAction;
    private bool _promptOnClose;
    private bool _startWithWindows;
    private bool _autoStartServerOnLaunch;
    private bool _autoCheckUpdate;

    private string? _messageLinkUrl;
    private string _messageLinkText = "";

    private UpdateInfo? _lastUpdateInfo;
    private bool _updateAvailable;
    private string _latestVersion = "";
    private string _updateStatusText = "";
    private bool _checkingUpdate;

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
        _closeAction = s.CloseAction;
        _promptOnClose = s.PromptOnClose;
        _startWithWindows = s.StartWithWindows;
        _autoStartServerOnLaunch = s.AutoStartServerOnLaunch;
        _autoCheckUpdate = s.AutoCheckUpdate;

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UpdateMeta();

        // 文本类设置防抖保存：停止输入约 0.8 秒后自动落盘
        _settingsDebounce = new DispatcherTimer { Interval = SettingsAutoSaveDelay };
        _settingsDebounce.Tick += (_, _) =>
        {
            _settingsDebounce.Stop();
            AutoSave();
        };

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

        ResetSettingsCommand = new RelayCommand(ResetSettings);
        BrowseRepoCommand = new RelayCommand(BrowseRepo);
        OpenDataDirCommand = new RelayCommand(OpenDataDir);
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        CopyLogCommand = new RelayCommand(CopyLog);
        OpenMessageLinkCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrEmpty(_messageLinkUrl)) return;
            try
            {
                BrowserOpener.Open(_messageLinkUrl);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, isError: true);
            }
        });
        CheckUpdateCommand = new RelayCommand(() => RunSafely(() => CheckForUpdatesAsync(manual: true)));
        UpdateNowCommand = new RelayCommand(() => RunSafely(UpdateNowAsync), () => _updateAvailable);

        // ── 服务层事件 ──────────────────────────────────────────────
        _manager.StateChanged += OnStateChanged;
        _manager.LogLine += OnLogLine;
        _manager.Ready += OnReady;
        _manager.Error += OnManagerError;
        _manager.LanUrlChanged += OnLanUrlChanged;

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

    /// <summary>文本类设置（防抖保存）：修改后停止输入约 0.8 秒自动落盘。</summary>
    public string RepoPath { get => _repoPath; set { _repoPath = value; OnPropertyChanged(); ScheduleAutoSave(); } }
    public string PortText { get => _portText; set { _portText = value; OnPropertyChanged(); ScheduleAutoSave(); } }

    /// <summary>绑定主机（下拉选择，改动立即保存；含 T7 局域网安全确认）。</summary>
    public string Host
    {
        get => _host;
        set { _host = value; OnPropertyChanged(); OnPropertyChanged(nameof(LanEnabled)); AutoSaveOnChange(); }
    }

    /// <summary>T7：当前是否对局域网开放（绑定 0.0.0.0），驱动设置页红色安全横幅。</summary>
    public bool LanEnabled => _host == "0.0.0.0";
    public string ExtraArgs { get => _extraArgs; set { _extraArgs = value; OnPropertyChanged(); ScheduleAutoSave(); } }

    /// <summary>即时保存项：改动立即落盘。</summary>
    public bool AutoOpenBrowser { get => _autoOpenBrowser; set { _autoOpenBrowser = value; OnPropertyChanged(); AutoSaveOnChange(); } }

    /// <summary>关闭主界面时的行为（互斥单选）。</summary>
    public CloseAction CloseAction
    {
        get => _closeAction;
        set
        {
            _closeAction = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseActionIsExit));
            OnPropertyChanged(nameof(CloseActionIsMinimize));
            AutoSaveOnChange();
        }
    }

    /// <summary>互斥单选：退出程序。</summary>
    public bool CloseActionIsExit
    {
        get => _closeAction == CloseAction.Exit;
        set { if (value) CloseAction = CloseAction.Exit; }
    }

    /// <summary>互斥单选：最小化到托盘。</summary>
    public bool CloseActionIsMinimize
    {
        get => _closeAction == CloseAction.MinimizeToTray;
        set { if (value) CloseAction = CloseAction.MinimizeToTray; }
    }

    /// <summary>关闭主界面时是否弹出选择对话框。</summary>
    public bool PromptOnClose { get => _promptOnClose; set { _promptOnClose = value; OnPropertyChanged(); AutoSaveOnChange(); } }

    public bool StartWithWindows { get => _startWithWindows; set { _startWithWindows = value; OnPropertyChanged(); AutoSaveOnChange(); } }

    /// <summary>打开软件时同时启动 dsh（T6）。</summary>
    public bool AutoStartServerOnLaunch { get => _autoStartServerOnLaunch; set { _autoStartServerOnLaunch = value; OnPropertyChanged(); AutoSaveOnChange(); } }

    /// <summary>启动时自动检查更新（T4）。</summary>
    public bool AutoCheckUpdate { get => _autoCheckUpdate; set { _autoCheckUpdate = value; OnPropertyChanged(); AutoSaveOnChange(); } }

    // ── 版本与更新（T4/T5） ─────────────────────────────────────────

    /// <summary>当前版本文本（如 "v0.3.0"）。</summary>
    public string CurrentVersionText => "v" + UpdateService.CurrentVersion;

    /// <summary>是否有可用更新（驱动标题栏更新徽标）。</summary>
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set { _updateAvailable = value; OnPropertyChanged(); OnPropertyChanged(nameof(UpdateBadgeText)); }
    }

    public string LatestVersion
    {
        get => _latestVersion;
        private set { _latestVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(UpdateBadgeText)); }
    }

    /// <summary>标题栏更新徽标文本（无更新时为空）。</summary>
    public string UpdateBadgeText => _updateAvailable ? $"有更新 v{_latestVersion} ↑" : "";

    /// <summary>更新状态文本（设置页显示）。</summary>
    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set { _updateStatusText = value; OnPropertyChanged(); }
    }

    // ── 消息条链接（T3 环境指引） ─────────────────────────────────

    public string? MessageLinkUrl => _messageLinkUrl;
    public string MessageLinkText => _messageLinkText;
    public bool HasMessageLink => _messageLinkUrl is not null;

    /// <summary>绑定主机选项（dsh webserver schema 仅允许 127.0.0.1 / 0.0.0.0）。</summary>
    public sealed record HostOption(string Display, string Value);

    public IReadOnlyList<HostOption> HostOptions { get; } = new[]
    {
        new HostOption("127.0.0.1（本机）", "127.0.0.1"),
        new HostOption("0.0.0.0（局域网共享）", "0.0.0.0"),
    };

    // ── 局域网地址（T7，从就绪行 LAN 部分解析） ─────────────────────

    private string? _lanUrl;

    /// <summary>局域网访问地址（如 http://192.168.1.5:3080），未解析时为空。</summary>
    public string? LanUrlText => _lanUrl;

    public bool HasLanUrl => _lanUrl is not null;

    // ── 命令 ────────────────────────────────────────────────────────

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand OpenBrowserCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand BrowseRepoCommand { get; }
    public RelayCommand OpenDataDirCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand OpenMessageLinkCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand UpdateNowCommand { get; }

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

    private void OnManagerError(EnvIssue issue) =>
        _ui.Post(_ =>
        {
            if (_isExiting) return;
            ShowMessage(issue.Message, isError: true, issue.LinkUrl, issue.LinkText);
            UpdateTrayTooltip();
        }, null);

    private void OnLanUrlChanged(string? url) =>
        _ui.Post(_ =>
        {
            if (_isExiting) return;
            _lanUrl = url;
            OnPropertyChanged(nameof(LanUrlText));
            OnPropertyChanged(nameof(HasLanUrl));
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

    /// <summary>
    /// 自动保存：校验文本输入 → 写回设置模型 → 落盘（含 T7 lan.yml 补丁联动）。
    /// 非法输入不落盘仅提示；勾选/单选/下拉即时触发，文本输入由防抖/失焦触发。
    /// </summary>
    private void AutoSave()
    {
        if (_suppressAutoSave) return;

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

        // T7：切换到 0.0.0.0（局域网共享）时弹一次安全确认；取消则回退选择且不保存。
        var newHost = string.IsNullOrWhiteSpace(_host) ? "127.0.0.1" : _host.Trim();
        if (newHost == "0.0.0.0" && s.Host != "0.0.0.0")
        {
            var confirm = MessageBox.Show(Window,
                "将对局域网开放（绑定 0.0.0.0）。\n\n" +
                "局域网内任何设备都能访问本服务并运行工具（SDK 会自动信任局域网 IP，无认证）。\n" +
                "请仅在可信网络开启。是否继续？",
                "局域网共享",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                Host = s.Host; // 取消：回退下拉选择
                return;
            }
        }

        s.RepoPath = _repoPath.Trim();
        s.Port = port;
        s.Host = newHost;
        s.ExtraArgs = _extraArgs.Trim();
        s.AutoOpenBrowser = _autoOpenBrowser;
        s.CloseAction = _closeAction;
        s.PromptOnClose = _promptOnClose;
        s.StartWithWindows = _startWithWindows;
        s.AutoStartServerOnLaunch = _autoStartServerOnLaunch;
        s.AutoCheckUpdate = _autoCheckUpdate;

        // T7 补丁联动：0.0.0.0 → 生成 lan.yml 挂载；否则删除
        if (newHost == "0.0.0.0") SettingsStore.WriteLanPatch();
        else SettingsStore.RemoveLanPatch();

        _settings.Save();
        _portText = s.Port.ToString();
        OnPropertyChanged(nameof(PortText));

        var running = _manager.State is ServerState.Starting or ServerState.Running;
        ShowMessage(running ? "设置已自动保存；运行中的参数将在下次重启后生效。" : "设置已自动保存。");
    }

    /// <summary>文本输入变化：重置防抖计时器（停止输入约 0.8 秒后自动保存）。</summary>
    private void ScheduleAutoSave()
    {
        if (_suppressAutoSave) return;
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
    }

    /// <summary>勾选/单选/下拉等即时项：改动立即保存。</summary>
    private void AutoSaveOnChange()
    {
        if (_suppressAutoSave) return;
        AutoSave();
    }

    /// <summary>文本输入失焦或窗口关闭前：立即保存（冲刷防抖计时器）。</summary>
    public void CommitPendingSettings()
    {
        _settingsDebounce.Stop();
        AutoSave();
    }

    private void ResetSettings()
    {
        _settingsDebounce.Stop();
        _suppressAutoSave = true;
        try
        {
            _settings.ResetToDefaults();
            var s = _settings.Current;
            RepoPath = s.RepoPath;
            PortText = s.Port.ToString();
            Host = s.Host;
            ExtraArgs = s.ExtraArgs;
            AutoOpenBrowser = s.AutoOpenBrowser;
            CloseAction = s.CloseAction;
            PromptOnClose = s.PromptOnClose;
            StartWithWindows = s.StartWithWindows;
            AutoStartServerOnLaunch = s.AutoStartServerOnLaunch;
            AutoCheckUpdate = s.AutoCheckUpdate;

            // T7 补丁联动：恢复默认（Host=127.0.0.1）后删除局域网补丁
            if (s.Host == "0.0.0.0") SettingsStore.WriteLanPatch();
            else SettingsStore.RemoveLanPatch();
        }
        finally
        {
            _suppressAutoSave = false;
        }
        _settings.Save();
        ShowMessage("已恢复默认设置并保存。");
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

    private void ShowMessage(string message, bool isError = false, string? linkUrl = null, string? linkText = null)
    {
        _messageText = message;
        _messageBrush = isError ? ErrorBrush : InfoBrush;
        _messageLinkUrl = linkUrl;
        // 只有带链接时才显示"点击打开"，否则留空（Hyperlink 不可见），避免出现死链接
        _messageLinkText = string.IsNullOrWhiteSpace(linkUrl) ? "" : (string.IsNullOrWhiteSpace(linkText) ? "点击打开" : linkText);
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(MessageBrush));
        OnPropertyChanged(nameof(MessageLinkUrl));
        OnPropertyChanged(nameof(MessageLinkText));
        OnPropertyChanged(nameof(HasMessageLink));
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
        UpdateNowCommand.RaiseCanExecuteChanged();
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

    private void ExitFromTray() => ExitApplication(ResolveServiceExit());

    /// <summary>服务运行时的退出决策。</summary>
    private enum ExitPlan { Proceed, StopAndExit, DetachAndExit, Cancel }

    /// <summary>服务运行时弹确认（停止并退出 / 保持运行并退出 / 取消）；未运行直接 Proceed。</summary>
    private ExitPlan ResolveServiceExit()
    {
        var state = _manager.State;
        if (state is not (ServerState.Starting or ServerState.Running)) return ExitPlan.Proceed;
        var choice = MessageBox.Show(Window,
            "DSH Web 服务正在运行。\n\n" +
            "是(Y)：停止服务并退出\n" +
            "否(N)：保持服务运行，仅退出启动器\n" +
            "取消：返回",
            "退出 AstesiaHarness",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        return choice switch
        {
            MessageBoxResult.Yes => ExitPlan.StopAndExit,
            MessageBoxResult.No => ExitPlan.DetachAndExit,
            _ => ExitPlan.Cancel,
        };
    }

    /// <summary>按退出决策执行退出（Cancel 忽略）。StopAndExit 异步停服后 Shutdown。</summary>
    private void ExitApplication(ExitPlan plan)
    {
        switch (plan)
        {
            case ExitPlan.DetachAndExit:
                _manager.DetachAndRelease();
                break;
            case ExitPlan.StopAndExit:
                IsExiting = true;
                _ = StopAndShutdownAsync();
                return;
            case ExitPlan.Cancel:
                return;
        }
        IsExiting = true;
        Application.Current.Shutdown();
    }

    private async Task StopAndShutdownAsync()
    {
        try { await _manager.StopAsync(); }
        finally { Application.Current.Shutdown(); }
    }

    /// <summary>主窗口关闭请求的结果。</summary>
    public enum WindowCloseResult
    {
        /// <summary>允许关闭（退出流程已触发）。</summary>
        Proceed,

        /// <summary>取消关闭并最小化到托盘。</summary>
        Minimize,

        /// <summary>取消关闭，窗口保持。</summary>
        Cancel,
    }

    /// <summary>
    /// 处理主窗口关闭请求（T1）：按设置执行「退出程序 / 最小化到托盘」；
    /// 开启「退出时提示」时弹选择对话框，「不再提示」写回设置。
    /// </summary>
    public WindowCloseResult HandleWindowClosing()
    {
        if (_isExiting) return WindowCloseResult.Proceed;

        CommitPendingSettings(); // 关闭/最小化前冲刷未落盘的文本输入

        var action = _closeAction;
        if (_promptOnClose)
        {
            var dialog = new ClosePromptDialog(action) { Owner = Window };
            if (dialog.ShowDialog() != true) return WindowCloseResult.Cancel;

            if (dialog.DontAskAgain)
            {
                // 「不再提示」：写回设置（持久生效），此后直接按所选执行。
                // 抑制属性触发的自动保存，仅在此处统一落盘一次（避免文本校验失败时丢失该改动）。
                _suppressAutoSave = true;
                try
                {
                    CloseAction = dialog.SelectedAction;
                    PromptOnClose = false;
                    var s = _settings.Current;
                    s.CloseAction = dialog.SelectedAction;
                    s.PromptOnClose = false;
                    _settings.Save();
                }
                finally
                {
                    _suppressAutoSave = false;
                }
            }
            action = dialog.SelectedAction;
        }

        if (action == CloseAction.Exit)
        {
            var plan = ResolveServiceExit();
            if (plan == ExitPlan.Cancel) return WindowCloseResult.Cancel;
            ExitApplication(plan);
            return WindowCloseResult.Proceed;
        }
        return WindowCloseResult.Minimize;
    }

    // ── 自动更新（T4） ─────────────────────────────────────────────

    /// <summary>
    /// 检查更新。manual=true：有更新时直接进入确认更新流程；
    /// manual=false（启动静默检查）：仅提示（驱动标题栏徽标），不自动下载。
    /// </summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        UpdateStatusText = "正在检查更新…";
        try
        {
            var info = await UpdateService.CheckForUpdatesAsync();
            _lastUpdateInfo = info;
            if (info is null)
            {
                UpdateStatusText = manual ? "检查更新失败（网络或仓库不可达）。" : "";
            }
            else if (!info.UpdateAvailable)
            {
                UpdateAvailable = false;
                UpdateStatusText = manual ? "已是最新版本。" : "";
            }
            else
            {
                LatestVersion = info.LatestVersion;
                UpdateAvailable = true;
                UpdateStatusText = $"发现新版本 v{info.LatestVersion}。";
                if (manual) await UpdateNowAsync();
            }
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    /// <summary>执行更新：确认 → 目录可写预检 → 下载 → SHA256 校验 → 退出并由 updater 覆盖重启。</summary>
    private async Task UpdateNowAsync()
    {
        var info = _lastUpdateInfo;
        if (info is null || !info.UpdateAvailable || string.IsNullOrEmpty(info.AssetUrl))
        {
            // 徽标点击路径可能尚无检查结果：先查一次
            await CheckForUpdatesAsync(manual: false);
            info = _lastUpdateInfo;
            if (info is null || !info.UpdateAvailable || string.IsNullOrEmpty(info.AssetUrl)) return;
        }

        var choice = MessageBox.Show(Window,
            $"发现新版本：v{UpdateService.CurrentVersion} → v{info.LatestVersion}\n\n" +
            "是否立即下载并更新？更新需要退出程序并自动重启。",
            "软件更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (choice != MessageBoxResult.Yes) return;

        if (!UpdateService.CanWriteExeDirectory())
        {
            ShowMessage("程序所在目录不可写，无法自动更新。请将程序移动到可写目录（如桌面、文档）后重试。", isError: true);
            return;
        }

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        var newPath = Path.Combine(exeDir, "AstesiaHarness.exe.update");
        try
        {
            UpdateStatusText = "正在下载更新…";
            var progress = new Progress<double>(p => UpdateStatusText = $"正在下载更新… {p:P0}");
            await UpdateService.DownloadAsync(info.AssetUrl, newPath, progress);

            UpdateStatusText = "正在校验完整性…";
            if (!await UpdateService.VerifySha256Async(newPath, info.AssetSha256Url))
            {
                TryDelete(newPath);
                UpdateStatusText = "更新文件校验失败，已取消（现有程序未受影响）。";
                ShowMessage("更新文件校验失败，已取消。", isError: true);
                return;
            }

            // 先处理服务退出决策（取消则中止更新，删除暂存文件）
            var plan = ResolveServiceExit();
            if (plan == ExitPlan.Cancel)
            {
                TryDelete(newPath);
                UpdateStatusText = "已取消更新。";
                return;
            }

            // 启动隐藏 updater（等待本进程退出后覆盖并重启），随后退出
            UpdateService.LaunchUpdaterAndExit();
            UpdateStatusText = "更新完成，正在重启…";
            ExitApplication(plan);
        }
        catch (Exception ex)
        {
            TryDelete(newPath);
            UpdateStatusText = "更新失败：" + ex.Message;
            ShowMessage("更新失败：" + ex.Message, isError: true);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    public void Dispose()
    {
        _manager.StateChanged -= OnStateChanged;
        _manager.LogLine -= OnLogLine;
        _manager.Ready -= OnReady;
        _manager.Error -= OnManagerError;
        _manager.LanUrlChanged -= OnLanUrlChanged;
        _uptimeTimer.Stop();
        _settingsDebounce.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
