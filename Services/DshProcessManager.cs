using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AstesiaHarness.Services;

/// <summary>
/// DSH Web 进程管理器：拉起 / 停止 / 重启 `pnpm dsh web`，全程无控制台窗口。
///
/// 进程树清理（architecture D-1）：
/// - 子进程创建后立即 AssignProcessToJobObject 到本进程持有的 Job（KILL_ON_JOB_CLOSE）；
/// - 停止 = TerminateJobObject，一键终结 pnpm → node → esbuild 整条链，无孤儿进程；
/// - 启动器异常退出时 Job 句柄关闭自动杀光进程。
/// </summary>
public sealed class DshProcessManager : IDisposable
{
    // ── Job Object 常量 ──────────────────────────────────────────────
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    // ── 时间常量 ─────────────────────────────────────────────────────
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _settings;

    private Process? _process;
    private IntPtr _jobHandle = IntPtr.Zero;
    private ReadinessWatcher? _watcher;
    private bool _stopping;          // 本次退出是否为主动停止
    private int _disposed;
    private readonly object _lifecycleLock = new();

    /// <summary>状态变化（线程池线程触发，UI 侧自行编组）。</summary>
    public event Action<ServerState>? StateChanged;

    /// <summary>日志行（text, isError）。</summary>
    public event Action<string, bool>? LogLine;

    /// <summary>服务就绪，参数为完整 URL。</summary>
    public event Action<string>? Ready;

    /// <summary>局域网地址变化（0.0.0.0 绑定时从就绪行解析，如 http://192.168.x.x:3080；null=不可用）。</summary>
    public event Action<string?>? LanUrlChanged;

    /// <summary>致命错误（含用户可读指引，可带链接）。</summary>
    public event Action<EnvIssue>? Error;

    /// <summary>当前状态。</summary>
    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>根进程 PID（Starting / Running / Failed 时有效）。</summary>
    public int? ProcessId { get; private set; }

    /// <summary>局域网访问地址（T7，从就绪行 LAN 部分解析）。</summary>
    public string? LanUrl { get; private set; }

    public DshProcessManager(SettingsStore settings) => _settings = settings;

    /// <summary>启动 DSH Web 服务。</summary>
    public async Task StartAsync()
    {
        lock (_lifecycleLock)
        {
            if (State is ServerState.Starting or ServerState.Running) return;
        }

        var settings = _settings.Current;
        SetState(ServerState.Starting);
        ClearProcessId();
        SetLanUrl(null);

        try
        {
            // 1) 环境自检（T3）：仓库路径 / Node 存在 / Node 版本，失败即给出指引并停止。
            var issue = await EnvironmentCheck.CheckAsync(settings);
            if (issue is not null)
            {
                Fail(issue);
                return;
            }

            // 1.5) 前端构建产物缺失提示（软提示，不阻断：就绪超时时仍会再次提示）。
            if (!EnvironmentCheck.HasFrontendDist(settings.RepoPath))
            {
                EmitLog("提示：未找到前端构建产物 apps/web/dist/index.html，若启动后一直未就绪，请在该仓库运行 pnpm build。");
            }

            // 1.6) T7 防御：Host=0.0.0.0 时确保 lan.yml 补丁存在（设置文件可能被手工修改）。
            if (settings.Host == "0.0.0.0" && !File.Exists(SettingsStore.LanYmlPath))
            {
                SettingsStore.WriteLanPatch();
            }

            // 2) 启动器解析：pnpm.cmd（PATH）→ 降级 node 直启。
            var launcher = ResolveLauncher(settings);
            if (launcher is null)
            {
                Fail(new EnvIssue(
                    "未找到 pnpm，且 PATH 中也没有 node.exe。请安装 Node.js（DSH 要求 ^22.19 || >=24）后重试。",
                    EnvironmentCheck.NodeDownloadUrl, "打开 Node.js 下载页"));
                return;
            }

            // 3) 端口探测：已运行则直接复用；被占用则失败。
            var portStatus = await PortProbe.ProbeAsync(settings.Host, settings.Port);
            switch (portStatus)
            {
                case PortStatus.DshRunning:
                    EmitLog($"检测到 DSH Web 已在 {settings.Host}:{settings.Port} 运行，直接复用，不再重复启动。");
                    SetState(ServerState.Running);
                    Ready?.Invoke($"http://{settings.Host}:{settings.Port}");
                    return;
                case PortStatus.OccupiedByOther:
                    Fail($"端口 {settings.Port} 已被其他程序占用（非 DSH Web 服务）。请关闭占用程序或在设置中更换端口。");
                    return;
            }

            // 5) 拉起进程（无控制台窗口，重定向输出）。
            var psi = new ProcessStartInfo
            {
                FileName = launcher.FileName,
                Arguments = launcher.Arguments,
                WorkingDirectory = settings.RepoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var key in new[] { "DSH_HOME", "PATH" })
            {
                if (Environment.GetEnvironmentVariable(key) is { } v) psi.Environment[key] = v;
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputData;
            _process.ErrorDataReceived += OnErrorData;
            _process.Exited += OnProcessExited;

            try
            {
                if (!_process.Start()) throw new InvalidOperationException("进程启动失败（Start 返回 false）。");
            }
            catch (Win32Exception ex)
            {
                Fail($"无法启动 {launcher.FileName}：{ex.Message}");
                return;
            }

            ProcessId = _process.Id;
            AssignToJob(_process);
            EmitLog($"已启动：{launcher.FileName} {launcher.Arguments}");
            EmitLog($"工作目录：{settings.RepoPath}");

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // 6) 就绪监听。
            _watcher = new ReadinessWatcher(settings.Host, settings.Port, ReadinessTimeout);
            _watcher.Ready += url =>
            {
                SetState(ServerState.Running);
                Ready?.Invoke(url);
            };
            _watcher.TimedOut += () =>
            {
                if (State == ServerState.Starting)
                {
                    Fail($"等待服务就绪超时（{ReadinessTimeout.TotalSeconds:0} 秒）。请查看日志，确认前端 dist 已构建（pnpm build）。");
                }
            };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Fail($"启动失败：{ex.Message}");
        }
    }

    /// <summary>停止服务：终结整个 Job 内的进程树，并用"端口兜底"确保服务确已停止。</summary>
    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (State is not (ServerState.Starting or ServerState.Running)) return Task.CompletedTask;
            _stopping = true;
        }
        return Task.Run(() =>
        {
            EmitLog("正在停止 DSH Web 服务…");
            // 复用外部服务（DshRunning 路径）时本启动器没有托管进程：停止只能按端口终止占用进程
            var ownedProcess = _process;
            if (ownedProcess is null)
            {
                EmitLog("该服务并非由本启动器启动（外部启动/复用路径），将按端口终止占用进程。", isError: true);
            }
            var job = _jobHandle;
            if (job != IntPtr.Zero)
            {
                TerminateJobObject(job, 1);
            }
            var proc = _process;
            if (proc is not null && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception ex) { EmitLog($"进程树终止失败：{ex.Message}", isError: true); }
            }
            try { _process?.WaitForExit((int)StopWaitTimeout.TotalMilliseconds); } catch (Exception) { }
            // 兜底：等待端口释放，未释放则按端口定位进程强杀（Job 未覆盖进程树时的最后防线）
            EnsurePortReleased(_settings.Current.Host, _settings.Current.Port);
            CleanupProcessResources();
            // 复用路径没有托管进程，Exited 事件永远不会触发 → 必须手动完成状态迁移，
            // 否则 State 停在 Running，停止按钮永不置灰、再点停止无效。
            if (ownedProcess is null)
            {
                _stopping = false;
                SetState(ServerState.Stopped);
                EmitLog("DSH Web 服务已停止。");
            }
        });
    }

    /// <summary>重启服务。</summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        _stopping = false;
        await StartAsync();
    }

    /// <summary>
    /// 脱离托管：清除 KILL_ON_JOB_CLOSE，随后应用退出不会带走服务进程
    /// （托盘"保持运行并退出"场景）。调用后本管理器不再管理该进程。
    /// </summary>
    public void DetachAndRelease()
    {
        lock (_lifecycleLock)
        {
            if (_jobHandle != IntPtr.Zero)
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0;
                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, ptr, (uint)size);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
            _watcher?.Dispose();
            _watcher = null;
            if (_process is not null)
            {
                _process.EnableRaisingEvents = false;
                _process = null;
            }
            ProcessId = null;
            SetState(ServerState.Stopped);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lifecycleLock)
        {
            if (_jobHandle != IntPtr.Zero) { TerminateJobObject(_jobHandle, 1); CloseHandle(_jobHandle); _jobHandle = IntPtr.Zero; }
            CleanupProcessResources();
        }
    }

    // ── 私有实现 ────────────────────────────────────────────────────

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        _watcher?.FeedLine(e.Data);
        TryParseLanUrl(e.Data);
        EmitLog(e.Data, isError: false);
    }

    private void OnErrorData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        _watcher?.FeedLine(e.Data); // 就绪行理论上走 stdout，双路喂入更稳
        EmitLog(e.Data, isError: true);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = _process?.ExitCode;
        var host = _settings.Current.Host;
        var port = _settings.Current.Port;
        CleanupProcessResources();

        // 兜底：根进程退出但服务端口仍被占用（典型：dsh 进程未纳入 Job 而成为孤儿）
        // → 按端口定位并强杀，避免"进程看似停止、服务仍在运行"。
        if (!_stopping && IsPortListening(host, port))
        {
            EmitLog("检测到根进程已退出但服务端口仍被占用，执行端口兜底终止…", isError: true);
            var pid = FindPidByPort(port);
            if (pid is > 0) KillPidTree(pid.Value);
        }

        if (_stopping)
        {
            _stopping = false;
            SetState(ServerState.Stopped);
            EmitLog("DSH Web 服务已停止。");
        }
        else if (State == ServerState.Starting)
        {
            Fail($"进程提前退出（退出码 {code}）。请查看日志。");
        }
        else if (State == ServerState.Running)
        {
            SetState(code == 0 ? ServerState.Stopped : ServerState.Failed);
            EmitLog($"进程退出（退出码 {code}）。");
        }
    }

    private void CleanupProcessResources()
    {
        if (_process is not null)
        {
            _process.OutputDataReceived -= OnOutputData;
            _process.ErrorDataReceived -= OnErrorData;
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
        _watcher?.Dispose();
        _watcher = null;
        if (_jobHandle != IntPtr.Zero) { CloseHandle(_jobHandle); _jobHandle = IntPtr.Zero; }
        ProcessId = null;
        SetLanUrl(null);
    }

    private static readonly Regex LanUrlPattern = new(@"LAN:\s*(https?://\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>从就绪行解析局域网地址（如 "dsh web: http://127.0.0.1:3080 (LAN: http://192.168.1.5:3080)"）。</summary>
    private void TryParseLanUrl(string line)
    {
        var m = LanUrlPattern.Match(line);
        if (!m.Success) return;
        var url = m.Groups[1].Value;
        if (LanUrl == url) return;
        LanUrl = url;
        LanUrlChanged?.Invoke(url);
    }

    private void SetLanUrl(string? url)
    {
        if (LanUrl == url) return;
        LanUrl = url;
        LanUrlChanged?.Invoke(url);
    }

    private void Fail(string message) => Fail(new EnvIssue(message));

    private void Fail(EnvIssue issue)
    {
        SetState(ServerState.Failed);
        ClearProcessId();
        EmitLog(issue.Message, isError: true);
        Error?.Invoke(issue);
    }

    private void SetState(ServerState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void ClearProcessId() => ProcessId = null;

    private void EmitLog(string text, bool isError = false) => LogLine?.Invoke(text, isError);

    // ── 端口兜底终止（Job 未覆盖进程树时的最后防线） ────────────────

    /// <summary>轮询等待端口释放；超时仍占用则按端口定位进程并强杀。</summary>
    private void EnsurePortReleased(string host, int port)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsPortListening(host, port)) return;
            Thread.Sleep(200);
        }

        EmitLog($"端口 {port} 在停止后仍被占用，执行端口兜底终止…", isError: true);
        var pid = FindPidByPort(port);
        if (pid is > 0)
        {
            EmitLog($"定位到占用进程 PID {pid}，正在终止其进程树。", isError: true);
            KillPidTree(pid.Value);
        }
        else
        {
            EmitLog("未能定位占用端口 {port} 的进程（可能无监听或权限不足）。", isError: true);
        }
    }

    /// <summary>目标端口是否有 TCP 监听（连接成功即认为在监听）。</summary>
    private static bool IsPortListening(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(300)) return false;
            return client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>终止指定 PID 的进程树；失败时回退 taskkill /T /F。</summary>
    private static void KillPidTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch (Exception) { }
        }
    }

    /// <summary>通过 GetExtendedTcpTable 查找监听指定端口的进程 PID（仅 LISTEN 行）。</summary>
    private static int? FindPidByPort(int port)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size == 0) return null;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return null;
            var numEntries = Marshal.ReadInt32(buffer);
            // MIB_TCPTABLE_OWNER_PID：DWORD 条目数后紧跟行数组（实测 x64 上亦无填充，行起始偏移恒为 4）
            const int rowsOffset = 4;
            for (var i = 0; i < numEntries; i++)
            {
                var row = IntPtr.Add(buffer, rowsOffset + i * 24);
                // MIB_TCPROW_OWNER_PID: state(0) localAddr(4) localPort(8,网络序) remoteAddr(12) remotePort(16) pid(20)
                var state = Marshal.ReadInt32(row, 0);
                var rawPort = Marshal.ReadInt32(row, 8) & 0xFFFF;
                var localPort = (ushort)((rawPort >> 8) | ((rawPort & 0xFF) << 8));
                if (state == MIB_TCP_STATE_LISTEN && localPort == port)
                {
                    return Marshal.ReadInt32(row, 20);
                }
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── 端口兜底 P/Invoke ─────────────────────────────────────────

    private const int AF_INET = 2;
    // TCP_TABLE_OWNER_PID_LISTENER = 3（只含 LISTEN 行；4 是 CONNECTIONS，无监听行）
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const int MIB_TCP_STATE_LISTEN = 2;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    // ── 启动器解析 ──────────────────────────────────────────────────

    private sealed record LauncherInfo(string FileName, string Arguments, string NodePath);

    private static LauncherInfo? ResolveLauncher(AppSettings settings)
    {
        var extra = string.IsNullOrWhiteSpace(settings.ExtraArgs) ? "" : " " + settings.ExtraArgs.Trim();
        // T7：Host=0.0.0.0 时不传 --host（会被 dsh CLI 守卫拒绝），改挂载自动生成的补丁；
        // 并剥离附加参数中的 --host 片段，防止用户手填 --host 0.0.0.0 误触发守卫。
        string portArgs;
        if (settings.Host == "0.0.0.0")
        {
            var stripped = StripHostArgs(extra);
            portArgs = $"--port {settings.Port} --patch \"{SettingsStore.LanYmlPath}\"{stripped}";
        }
        else
        {
            portArgs = $"--port {settings.Port} --host {settings.Host}{extra}";
        }

        // 优先 pnpm：等价于用户手敲 `pnpm dsh web …`。
        var pnpm = EnvironmentCheck.FindPnpmPath();
        if (pnpm is not null)
        {
            var node = EnvironmentCheck.FindNodePath() ?? "node";
            return new LauncherInfo(pnpm, $"dsh web {portArgs}", node);
        }

        // 降级：直接 node 直启（与 pnpm 内部执行等价）。
        var nodePath = EnvironmentCheck.FindNodePath();
        if (nodePath is not null)
        {
            return new LauncherInfo(nodePath, $"--import tsx/esm apps/cli/src/bin.ts web {portArgs}", nodePath);
        }
        return null;
    }

    /// <summary>剥离参数串中的 --host 与 --host= 片段（含带引号的值）。</summary>
    private static string StripHostArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return "";
        return Regex.Replace(args, @"\s*--host(?:=|\s+)(?:""[^""]*""|\S+)", "", RegexOptions.IgnoreCase);
    }

    // ── Job Object P/Invoke ─────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private void AssignToJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            EmitLog("警告：Job Object 创建失败，停止时改用进程树/端口兜底终止。", isError: true);
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                EmitLog("警告：Job 参数设置失败（KILL_ON_JOB_CLOSE 未生效），停止时改用兜底终止。", isError: true);
                CloseHandle(job);
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        if (!AssignProcessToJobObject(job, process.Handle))
        {
            // 进程可能已退出（ERROR_ACCESS_DENIED）；或进程已属于其他 Job（嵌套被拒）——
            // 此时 Job 无进程，直接关闭；停止时端口兜底会接管。
            EmitLog("警告：进程未能纳入 Job（可能已退出或已属于其他 Job），停止时改用进程树/端口兜底终止。", isError: true);
            CloseHandle(job);
            return;
        }
        _jobHandle = job;
    }
}
