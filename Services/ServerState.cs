namespace AstesiaHarness.Services;

/// <summary>DSH Web 服务运行状态。</summary>
public enum ServerState
{
    /// <summary>未启动。</summary>
    Stopped,

    /// <summary>启动中（进程已拉起，等待就绪信号）。</summary>
    Starting,

    /// <summary>运行中（就绪，可访问）。</summary>
    Running,

    /// <summary>启动失败（端口占用 / 命令缺失 / 就绪超时 / 进程提前退出）。</summary>
    Failed,
}
