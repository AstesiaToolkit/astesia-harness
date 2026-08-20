namespace AstesiaHarness.Services;

/// <summary>关闭主界面时的行为（T1：互斥选取）。</summary>
public enum CloseAction
{
    /// <summary>最小化到托盘，服务保持运行。</summary>
    MinimizeToTray,

    /// <summary>退出程序（服务在运行时按退出确认流程处理）。</summary>
    Exit,
}

/// <summary>应用设置模型（与 settings.json 一一对应）。</summary>
public sealed class AppSettings
{
    /// <summary>DSH 仓库根目录（pnpm dsh web 的工作目录）。</summary>
    public string RepoPath { get; set; } = @"F:\Codes\DownloadProjects\deepseek-harness";

    /// <summary>服务端口（透传 --port）。</summary>
    public int Port { get; set; } = 3080;

    /// <summary>绑定主机（透传 --host；DSH 拒绝 0.0.0.0）。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>附加透传参数（空格分隔，追加到命令尾部）。</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>就绪后自动打开浏览器。</summary>
    public bool AutoOpenBrowser { get; set; } = true;

    /// <summary>关闭主界面时的行为（退出程序 / 最小化到托盘）。</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.MinimizeToTray;

    /// <summary>关闭主界面时是否弹出选择对话框（勾选后每次点 ✕ 都询问）。</summary>
    public bool PromptOnClose { get; set; } = false;

    /// <summary>开机自启（HKCU Run 键，--minimized 启动）。</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>打开软件时同时启动 dsh（T6）。</summary>
    public bool AutoStartServerOnLaunch { get; set; } = false;

    /// <summary>启动时自动检查更新（T4，仅提示不自动下载）。</summary>
    public bool AutoCheckUpdate { get; set; } = true;
}
