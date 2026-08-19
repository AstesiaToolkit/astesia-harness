using System.Net.Http;
using System.Text.RegularExpressions;

namespace AstesiaHarness.Services;

/// <summary>
/// 就绪判定（requirements FR-2 / architecture D-2）：
/// 主信号 = 解析子进程 stdout 中的官方就绪行 `dsh web: http://…`；
/// 兜底   = 周期性 HTTP GET /，收到 200 视为就绪。两者取先到。
/// </summary>
public sealed class ReadinessWatcher : IDisposable
{
    private static readonly Regex UrlLinePattern = new(@"dsh web:\s*(https?://\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;

    private readonly System.Threading.Timer _pollTimer;
    private readonly System.Threading.Timer _timeoutTimer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private bool _ready;
    private bool _polling;
    private int _disposed;

    /// <summary>就绪时触发，参数为完整 URL。</summary>
    public event Action<string>? Ready;

    /// <summary>超过 <see cref="_timeout"/> 仍未就绪时触发。</summary>
    public event Action? TimedOut;

    public ReadinessWatcher(string host, int port, TimeSpan timeout)
    {
        _host = host;
        _port = port;
        _timeout = timeout;
        _pollTimer = new System.Threading.Timer(OnPollTick, null, Timeout.Infinite, Timeout.Infinite);
        _timeoutTimer = new System.Threading.Timer(OnTimeout, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (_ready) return;
        _pollTimer.Change(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
        _timeoutTimer.Change(_timeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>喂入子进程 stdout 行，检测就绪行。</summary>
    public void FeedLine(string line)
    {
        if (_ready) return;
        var match = UrlLinePattern.Match(line);
        if (match.Success) Complete(match.Groups[1].Value);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pollTimer.Dispose();
        _timeoutTimer.Dispose();
        _http.Dispose();
    }

    private void Complete(string url)
    {
        if (_ready) return;
        _ready = true;
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _timeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Ready?.Invoke(url);
    }

    private async void OnPollTick(object? state)
    {
        if (_ready || _polling) return;
        _polling = true;
        try
        {
            using var resp = await _http.GetAsync($"http://{_host}:{_port}/");
            if (resp.IsSuccessStatusCode) Complete($"http://{_host}:{_port}");
        }
        catch (Exception)
        {
            // 服务尚未就绪，继续轮询。
        }
        finally
        {
            _polling = false;
        }
    }

    private void OnTimeout(object? state)
    {
        if (_ready) return;
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        TimedOut?.Invoke();
    }
}
