namespace AstesiaHarness.Services;

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

    /// <summary>关闭主窗口时最小化到托盘而非退出。</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>开机自启（HKCU Run 键，--minimized 启动）。</summary>
    public bool StartWithWindows { get; set; } = false;
}
