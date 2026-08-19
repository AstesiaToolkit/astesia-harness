using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace AstesiaHarness;

/// <summary>
/// 应用入口：单实例守卫、托盘常驻、窗口生命周期管理。
/// 关闭主窗口不退出进程（最小化到托盘），仅托盘"退出"显式 Shutdown。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "AstesiaHarness.SingleInstance";
    private const string ActivateEventName = "AstesiaHarness.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateHandle;
    private Thread? _activateThread;

    /// <summary>本次启动是否带 --minimized（开机自启场景：直接进托盘，不显示主窗口）。</summary>
    public bool StartMinimized { get; private set; }

    /// <summary>全局主视图模型（窗口与托盘共享）。</summary>
    public ViewModels.MainViewModel? MainViewModel { get; private set; }

    /// <summary>全局托盘图标。</summary>
    public Tray.TrayIcon? Tray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        // ── 单实例守卫 ──────────────────────────────────────────────
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在跑：通知它把窗口带到前台，然后本实例退出。
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); }
            catch (Exception) { /* 首实例可能正在启动中，忽略 */ }
            Shutdown();
            return;
        }

        // 二次启动时被唤醒：显示并聚焦主窗口。
        _activateHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateThread = new Thread(ActivateLoop) { IsBackground = true, Name = "activate-listener" };
        _activateThread.Start();

        // ── 应用主体 ────────────────────────────────────────────────
        var settingsStore = new Services.SettingsStore();
        settingsStore.Load();
        var manager = new Services.DshProcessManager(settingsStore);
        MainViewModel = new ViewModels.MainViewModel(settingsStore, manager);
        Tray = new Tray.TrayIcon(MainViewModel);
        MainViewModel.Tray = Tray; // 托盘 tooltip 随状态更新

        var window = new MainWindow { DataContext = MainViewModel };
        MainViewModel.Window = window;
        MainWindow = window;

        if (!StartMinimized)
        {
            window.Show();
            window.Activate();
        }
        else
        {
            // 开机自启：托盘常驻，主窗口隐藏，待用户点击托盘唤起。
            Tray.ShowBalloon("AstesiaHarness 已启动", "DeepSeek Harness 快速启动器正在托盘运行，点击图标打开主窗口。");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateHandle?.Set(); // 唤醒监听线程使其退出
        _activateHandle?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Tray?.Dispose();
        MainViewModel?.Dispose();
        base.OnExit(e);
    }

    private void ActivateLoop()
    {
        var handle = _activateHandle;
        if (handle is null) return;
        try
        {
            while (handle.WaitOne())
            {
                Dispatcher.Invoke(() =>
                {
                    var w = MainWindow;
                    if (w is null) return;
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Show();
                    w.Activate();
                    w.Topmost = true;
                    w.Topmost = false;
                    w.Focus();
                });
            }
        }
        catch (Exception)
        {
            // 应用退出过程中句柄被释放，忽略。
        }
    }
}
