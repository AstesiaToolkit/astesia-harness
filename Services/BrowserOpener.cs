using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace AstesiaHarness.Services;

/// <summary>
/// 浏览器打开：
/// - OpenOrFocus：默认浏览器打开；若 Chromium 浏览器（Chrome/Edge）已打开目标页面，切换并聚焦已有标签页（避免重复开标签）。
/// - OpenAppMode：Edge 应用窗口（--app=）打开；已存在应用模式窗口则聚焦复用。
/// 窗口枚举统一用 EnumWindows（每进程全量顶层窗口），修正 Process.MainWindowHandle 每进程只能取一个窗口的局限。
/// </summary>
public static class BrowserOpener
{
    private static readonly string[] ChromiumProcessNames = { "chrome", "msedge" };

    /// <summary>DSH 页面固定标题（apps/web/dist/index.html 的 &lt;title&gt;），标签匹配用。</summary>
    private const string DefaultTitleMarker = "DeepSeek Harness";

    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;

    /// <summary>最小化窗口恢复后，等待 Chromium 重建辅助功能树的时间。</summary>
    private const int MinimizedRebuildDelayMs = 300;

    /// <summary>
    /// 打开 URL（浏览器标签模式）：优先切换已打开的 Chromium 标签页；找不到则回退默认浏览器新开。
    /// </summary>
    public static void OpenOrFocus(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (TryFocusExistingTab(url, DefaultTitleMarker)) return;
        }
        catch (Exception)
        {
            // UIA 异常不影响兜底打开
        }
        Open(url);
    }

    /// <summary>
    /// 打开 URL（Edge 应用窗口模式，T2）：
    /// 1) 已存在应用模式窗口（标题匹配且 UIA 无 Tab 控件）→ 恢复并聚焦，不重复新开（含 PWA 窗口）；
    /// 2) 未找到 → 定位 Edge 并以 --app= 启动独立应用窗口；
    /// 3) 无 Edge → 回退默认浏览器（Open）。
    /// </summary>
    public static void OpenAppMode(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (TryFocusExistingAppWindow(url)) return;
            var edge = FindEdgePath();
            if (edge is null)
            {
                Open(url); // Edge 缺失 → 回退默认浏览器
                return;
            }
            Process.Start(new ProcessStartInfo(edge, $"--app=\"{url}\" --profile-directory=Default")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception)
        {
            // 任何异常都不影响兜底
            Open(url);
        }
    }

    /// <summary>默认浏览器打开（新标签页）。</summary>
    public static void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法打开浏览器：{ex.Message}", ex);
        }
    }

    // ── 标签模式 ──────────────────────────────────────────────────

    private static bool TryFocusExistingTab(string url, string titleMarker)
    {
        var hostPort = ExtractHostPort(url);

        // 收集候选窗口（每进程全量顶层窗口；非最小化优先，避免无谓恢复）。
        var windows = new List<(IntPtr Handle, string Title, bool Minimized)>();
        foreach (var name in ChromiumProcessNames)
        {
            foreach (var (hwnd, title) in EnumerateBrowserWindows(name))
            {
                windows.Add((hwnd, title, IsIconic(hwnd)));
            }
        }
        windows.Sort((a, b) => a.Minimized.CompareTo(b.Minimized));

        foreach (var (handle, title, wasMinimized) in windows)
        {
            try
            {
                // 快速路径：窗口标题已含目标页面（激活标签即目标页）→ 直接恢复并前置，无需 UIA。
                if (IsMatch(title, hostPort, titleMarker))
                {
                    BringToFront(handle);
                    return true;
                }

                // 最小化窗口：Chromium 会挂起辅助功能树（实证：最小化时 UIA 扫不到标签），
                // 先恢复窗口并等待其重建辅助功能树，再扫描。
                var restored = false;
                if (wasMinimized)
                {
                    ShowWindow(handle, SW_RESTORE);
                    restored = true;
                    Thread.Sleep(MinimizedRebuildDelayMs);
                }

                var root = AutomationElement.FromHandle(handle);
                if (root is not null)
                {
                    // 候选 1：标签栏（Tab 控件）的直接子 TabItem —— 最精准。
                    var tabControl = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
                    if (tabControl is not null)
                    {
                        var tabs = tabControl.FindAll(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                        if (TrySelectMatchingTab(tabs, handle, hostPort, titleMarker)) return true;
                    }

                    // 候选 2：窗口内全部 TabItem，按标题/地址过滤（覆盖标签栏未暴露为 Tab 控件的情况）。
                    var allTabs = root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                    if (TrySelectMatchingTab(allTabs, handle, hostPort, titleMarker)) return true;
                }

                // 未命中且窗口是我们恢复的 → 还原最小化，不打扰用户。
                if (restored) ShowWindow(handle, SW_MINIMIZE);
            }
            catch (Exception)
            {
                // 单个窗口探测失败，继续下一个
            }
        }
        return false;
    }

    // ── 应用窗口模式（T2） ────────────────────────────────────────

    /// <summary>查找已存在的应用模式窗口：标题匹配 且 UIA 树无 Tab 控件（应用窗口无标签栏）。</summary>
    private static bool TryFocusExistingAppWindow(string url)
    {
        var hostPort = ExtractHostPort(url);
        foreach (var name in ChromiumProcessNames)
        {
            foreach (var (hwnd, title) in EnumerateBrowserWindows(name))
            {
                if (!IsMatch(title, hostPort, DefaultTitleMarker)) continue;

                var minimized = IsIconic(hwnd);
                if (minimized)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    Thread.Sleep(MinimizedRebuildDelayMs);
                }

                try
                {
                    var root = AutomationElement.FromHandle(hwnd);
                    if (root is null) continue;
                    var tabControl = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
                    if (tabControl is null)
                    {
                        // 无标签栏 → 应用模式窗口（或 PWA 窗口）→ 聚焦复用
                        BringToFront(hwnd);
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 单窗口探测失败继续下一个
                }
            }
        }
        return false;
    }

    /// <summary>定位 Edge 可执行文件（x86 优先，回退 x64）。</summary>
    private static string? FindEdgePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ── 窗口枚举与匹配 ────────────────────────────────────────────

    /// <summary>枚举指定进程名的全部可见顶层窗口（EnumWindows 全量，修复 MainWindowHandle 每进程仅一个窗口的局限）。</summary>
    private static IEnumerable<(IntPtr Hwnd, string Title)> EnumerateBrowserWindows(string processName)
    {
        var pids = new HashSet<int>();
        try
        {
            foreach (var p in Process.GetProcessesByName(processName)) pids.Add(p.Id);
        }
        catch (Exception)
        {
            return Array.Empty<(IntPtr, string)>();
        }
        if (pids.Count == 0) return Array.Empty<(IntPtr, string)>();

        var result = new List<(IntPtr, string)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid)) return true;
            if (!IsWindowVisible(hwnd)) return true;
            var title = GetWindowText(hwnd);
            if (string.IsNullOrEmpty(title)) return true;
            result.Add((hwnd, title));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool IsMatch(string? text, string hostPort, string titleMarker)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains(titleMarker, StringComparison.OrdinalIgnoreCase)
            || (hostPort.Length > 0 && text.Contains(hostPort, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TrySelectMatchingTab(
        AutomationElementCollection tabs, IntPtr windowHandle, string hostPort, string titleMarker)
    {
        foreach (AutomationElement tab in tabs)
        {
            var text = tab.Current.Name ?? string.Empty;
            if (!IsMatch(text, hostPort, titleMarker)) continue;

            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                && pattern is SelectionItemPattern selection)
            {
                selection.Select();
                BringToFront(windowHandle);
                return true;
            }
        }
        return false;
    }

    /// <summary>从 URL 提取 "host" 或 "host:port"，用于标签标题为地址时的匹配。</summary>
    private static string ExtractHostPort(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}").ToLowerInvariant();
            }
        }
        catch (Exception) { }
        return string.Empty;
    }

    /// <summary>恢复并前置浏览器窗口（含 AttachThreadInput 兼容性处理）。</summary>
    private static void BringToFront(IntPtr windowHandle)
    {
        try
        {
            if (IsIconic(windowHandle)) ShowWindow(windowHandle, SW_RESTORE);
            var foreground = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var currentThread = GetCurrentThreadId();
            if (foregroundThread != currentThread)
            {
                AttachThreadInput(currentThread, foregroundThread, true);
                try { SetForegroundWindow(windowHandle); }
                finally { AttachThreadInput(currentThread, foregroundThread, false); }
            }
            else
            {
                SetForegroundWindow(windowHandle);
            }
        }
        catch (Exception)
        {
            // 前置失败不影响标签已选中
        }
    }

    // ── Win32 ─────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
}
