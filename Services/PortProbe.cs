using System.Net.Http;
using System.Net.Sockets;

namespace AstesiaHarness.Services;

/// <summary>端口探测结果。</summary>
public enum PortStatus
{
    /// <summary>端口空闲，可启动。</summary>
    Free,

    /// <summary>端口已被 DSH Web 服务占用（HTTP 200 且页面含 DeepSeek Harness 特征），直接复用。</summary>
    DshRunning,

    /// <summary>端口被其他程序占用，不强行启动。</summary>
    OccupiedByOther,
}

/// <summary>
/// 端口探测：启动前判定"空闲 / 已被 DSH 占用 / 被其他程序占用"（requirements FR-7）。
/// </summary>
public static class PortProbe
{
    private const string DshPageMarker = "DeepSeek Harness";

    public static async Task<PortStatus> ProbeAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        // 1) TCP 连接探测：连不上 → 空闲。
        using var tcp = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(host, port, timeoutCts.Token);
        }
        catch (Exception)
        {
            return PortStatus.Free;
        }

        // 2) TCP 通了 → HTTP GET /：200 且页面含 DSH 特征 → 已运行；否则判为其他程序。
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync($"http://{host}:{port}/", cancellationToken);
            if (!resp.IsSuccessStatusCode) return PortStatus.OccupiedByOther;
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            return body.Contains(DshPageMarker, StringComparison.OrdinalIgnoreCase)
                ? PortStatus.DshRunning
                : PortStatus.OccupiedByOther;
        }
        catch (Exception)
        {
            // TCP 通但 HTTP 握手失败：视为被其他程序占用（如纯 TCP 服务）。
            return PortStatus.OccupiedByOther;
        }
    }
}
