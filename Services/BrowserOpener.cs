using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace AstesiaHarness.Services;

/// <summary>
/// 浏览器打开：默认浏览器新开标签页；若 Chromium 浏览器（Chrome/Edge）已打开目标页面，
/// 则切换并聚焦已有标签页而非新开（避免重复开标签）。
/// </summary>
public static class BrowserOpener
{
    private static readonly string[] ChromiumProcessNames = { "chrome", "msedge" };

    /// <summary>DSH 页面固定标题（apps/web/dist/index.html 的 &lt;title&gt;），标签匹配用。</summary>
    private const string DefaultTitleMarker = "DeepSeek Harness";

    private const int SW_RESTORE = 9;

    /// <summary>
    /// 打开 URL：优先切换已打开的 Chromium 标签页；找不到则回退默认浏览器新开。
    /// UIA 探测失败不影响兜底，任何异常都不会向上抛。
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

    private static bool TryFocusExistingTab(string url, string titleMarker)
    {
        var hostPort = ExtractHostPort(url);

        foreach (var name in ChromiumProcessNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (Exception) { continue; }

            foreach (var proc in processes)
            {
                AutomationElement? root = null;
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;
                    root = AutomationElement.FromHandle(proc.MainWindowHandle);
                    if (root is null) continue;

                    // 候选 1：标签栏（Tab 控件）的直接子 TabItem —— 最精准。
                    var tabControl = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
                    if (tabControl is not null)
                    {
                        var tabs = tabControl.FindAll(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                        if (TrySelectMatchingTab(tabs, proc.MainWindowHandle, hostPort, titleMarker)) return true;
                    }

                    // 候选 2：窗口内全部 TabItem，按标题/地址过滤（覆盖标签栏未暴露为 Tab 控件的情况）。
                    var allTabs = root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                    if (TrySelectMatchingTab(allTabs, proc.MainWindowHandle, hostPort, titleMarker)) return true;
                }
                catch (Exception)
                {
                    // 单个窗口探测失败，继续下一个
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        return false;
    }

    private static bool TrySelectMatchingTab(
        AutomationElementCollection tabs, IntPtr windowHandle, string hostPort, string titleMarker)
    {
        foreach (AutomationElement tab in tabs)
        {
            var text = tab.Current.Name ?? string.Empty;
            var matches = text.Contains(titleMarker, StringComparison.OrdinalIgnoreCase)
                || (hostPort.Length > 0 && text.Contains(hostPort, StringComparison.OrdinalIgnoreCase));
            if (!matches) continue;

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
