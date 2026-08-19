# AstesiaHarness 架构设计与技术选型

> 版本: v1.0（设计阶段）
> 依赖: [requirements.md](./requirements.md)

---

## 1. 程序设计语言选型

### 1.1 候选方案对比

| 方案 | 产物体积 | 内存占用 | 进程/托盘/单实例支持 | 本机工具链 | 新增依赖 | 综合 |
| --- | --- | --- | --- | --- | --- | --- |
| **Tauri 2**（Rust 后端 + Web 前端） | ~10 MB | 低 | 优秀（tray、单实例、进程管理） | 需 Rust（本机无 cargo） | 需安装 rustup 工具链 | UI 现代、体积小，但前置安装重 |
| **Electron**（TypeScript） | ~200 MB | 高（数百 MB） | 良好 | node 24 / npm 11 齐备 | 无 | 生态熟、与 DSH 同语言，但对一个启动器过重 |
| **.NET 8 WPF**（C#） | 单文件 ~80 MB 或框架依赖 ~1 MB | 低（~50 MB） | **原生优秀**（Job Object、NotifyIcon、Mutex、Process） | .NET SDK 8.0.204 已装 | **无** | 与 Windows 桌面场景最贴合，零新增工具链 |
| **Python + PySide6** | 打包 ~60–100 MB | 中 | 良好（QSystemTrayIcon） | Python 3.11 已装 | 需 pip 装 PySide6、PyInstaller | 开发快，但分发与打包体验差 |
| **Go + systray/webview** | ~10 MB | 低 | 一般（UI 依赖 webview，托盘库维护一般） | 本机无 Go | 需安装 Go | 不推荐 |

### 1.2 决策：**.NET 8 WPF（C#）**

理由：

1. **零新增工具链**：本机已装 .NET SDK 8.0.204，`dotnet` 直接可建可发布；其他方案（Tauri/Go）都要先装几 GB 工具链。
2. **Windows 原生能力全覆盖本需求的关键点**：
   - 进程树管理 → Windows **Job Object**（P/Invoke），`KILL_ON_JOB_CLOSE` + `TerminateJobObject` 彻底清理 pnpm→node→esbuild 链，防孤儿进程（NFR/FR-4 核心）；
   - 托盘 → `System.Windows.Forms.NotifyIcon`；
   - 单实例 → 命名 `Mutex`；
   - 自动开浏览器 → `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`；
   - 开机自启 → 写 `HKCU\...\Run` 注册表。
3. **轻量**：WPF 常驻内存 ~50 MB，框架依赖发布产物 < 1 MB（+ 用户机器需 .NET 8 Desktop Runtime，本机已具备）；满足 NFR-2/7。
4. **单语言单工程**：C# + XAML 一个项目搞定 UI 与系统逻辑，无需前后端两个工程。

> **备选（若用户明确偏好现代 Web UI 且愿意安装 Rust）**：Tauri 2。架构分层与下方一致，仅把 UI 层换成 Web 前端、进程管理用 Rust 侧 `std::process` + `windows` crate 的 Job Object 实现。

---

## 2. 总体架构

```
┌──────────────────────── AstesiaHarness（WPF 单进程） ────────────────────────┐
│                                                                            │
│  App（Application）                                                        │
│   ├─ 单实例：命名 Mutex "AstesiaHarness.SingleInstance"，二次启动聚焦首实例    │
│   ├─ 启动参数：--minimized（开机自启时直接进托盘）                            │
│   ├─ MainWindow ── 状态区 │ URL 区 │ 操作按钮 │ 日志视图 │ 设置面板          │
│   └─ TrayIcon（NotifyIcon）── 状态色图标 + 左键聚焦 + 右键菜单               │
│                                                                            │
│  Services（服务层，全部可单元测试，不依赖 UI）                                │
│   ├─ DshProcessManager ── 进程生命周期核心                                   │
│   │    └─ 子进程：pnpm.cmd dsh web [--port N] [--host H] [extra...]          │
│   │       （cwd = repoPath，CreateNoWindow，重定向 stdout/stderr）           │
│   ├─ ReadinessWatcher ── 就绪判定（stdout 正则 + HTTP 轮询兜底）             │
│   ├─ PortProbe ── 端口探测（占用检测 / 已运行判定）                           │
│   ├─ BrowserOpener ── 调起系统默认浏览器                                      │
│   └─ SettingsStore ── %APPDATA%\AstesiaHarness\settings.json（JSON）         │
│                                                                            │
│  事件流：UI/Tray ──命令──▶ Services ──事件──▶ UI（状态、日志、URL 更新）       │
└────────────────────────────────────────────────────────────────────────────┘
```

### 2.1 分层原则

- **UI 层（View）**：XAML 窗口 + 托盘；只做展示与命令转发。
- **服务层（Services）**：纯 C# 逻辑，通过事件（`ProcessExited`、`Ready(url)`、`LogLine(line)`、`StateChanged(state)`）向 UI 广播；不持有窗体引用，便于单元测试。
- **状态模型（State）**：`Stopped → Starting → Running ⇄ Stopped`，异常分支 `Failed`。

---

## 3. 核心设计决策

### D-1 进程树管理：Windows Job Object（核心）

- 子进程创建后立即 `AssignProcessToJobObject` 到本进程持有的 Job，Job 设置 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`；
- **停止**：`TerminateJobObject` —— 一键终结整棵进程树（pnpm → node → esbuild worker），与"终端 Ctrl+C"等效且更彻底；
- **兜底**：Job 关闭时自动杀光进程（防启动器异常退出后残留孤儿）；
- **备选方案**：`taskkill /PID <rootPid> /T /F`（简单但依赖外部命令，作为 Job Object 失效时的 fallback）。

### D-2 就绪判定：stdout 就绪行 + HTTP 兜底

- **主信号**：正则匹配 stdout 行 `dsh web: http://(\S+)`。这是 DSH 官方就绪信号（Loader 树稳定后才打印，见 `packages/bundle/web-app/src/index.ts:159-184`），比轮询更早、更准；
- **兜底**：每 500 ms 对 `http://<host>:<port>/` 发起 GET，连续 1 次 200 即就绪；兜底覆盖 stdout 被吞的场景；
- **超时**：默认 120 s 未就绪 → `Failed` 并给出日志尾部提示；
- 就绪后回调 `Ready(url)` → 若 `autoOpenBrowser` 则调 `BrowserOpener`。

### D-3 已运行检测（FR-7）

启动前调用 `PortProbe`：
1. 端口无监听 → 正常启动；
2. 端口有监听且 HTTP 200 → 判定 DSH 已运行 → 不 spawn，直接 `Running` + 打开浏览器；
3. 端口有监听但非 DSH 响应（4xx/5xx/非 HTTP）→ 提示"端口被其他程序占用"，`Failed`，不强行启动。

### D-4 启动命令与参数拼装

- 默认：`pnpm.cmd dsh web --port <port> [--host <host>] [extraArgs...]`（与用户手工操作一致）；
- 工作目录 = `settings.repoPath`（校验存在，否则 FR-12 提示）；
- `pnpm.cmd` 找不到时自动降级：直接执行 `node --import tsx/esm apps/cli/src/bin.ts web ...`（与 pnpm 内部调用等价，见 `package.json` 的 `"dsh"` script）；
- 全部通过 `RedirectStandardOutput/Error` 采集，`CreateNoWindow = true`。

### D-5 日志采集与缓冲

- 子进程 stdout/stderr 各自 `DataReceived` 事件 → 统一 `LogLine` 事件（环形缓冲 5000 行）；
- UI 日志视图绑定缓冲，自动滚动；
- 可选落盘：`%APPDATA%\AstesiaHarness\logs\dsh-web.log`（追加 + 按天轮转）。

### D-6 设置存储

- 路径：`%APPDATA%\AstesiaHarness\settings.json`；
- `SettingsStore` 负责读写、默认值注入、非法值修复；
- 保存后若服务在跑，提示"重启后生效"（端口等参数仅启动时生效）。

### D-7 开机自启

- 写/删 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 键值 `AstesiaHarness` → `"<exe>" --minimized`；
- 与设置项 `startWithWindows` 双向同步。

### D-8 单实例与窗口行为

- 启动时 `Mutex.TryOpenExisting` + 新建 Mutex 判定；
- 二次实例：向首实例发送窗口激活消息（命名事件 + `SetForegroundWindow`）后退出；
- 窗口关闭 → 默认最小化到托盘（`minimizeToTrayOnClose`）；托盘"退出"时若服务在跑，弹出确认"是否同时停止 DSH Web 服务"。

---

## 4. 工程结构

```
AstesiaHarness/                        # F:\Codes\AstesiaToolkit\AstesiaHarness
├─ Assets/
│  └─ app.ico                          # ← 用户提供的主图标（exe / 窗口 / 托盘三处共用）
├─ AstesiaHarness.csproj               # net8.0-windows; UseWPF; 单文件发布配置
├─ App.xaml / App.xaml.cs              # 入口、单实例、--minimized
├─ MainWindow.xaml / MainWindow.xaml.cs
├─ Services/
│  ├─ DshProcessManager.cs             # 进程生命周期 + Job Object
│  ├─ ReadinessWatcher.cs              # 就绪判定
│  ├─ PortProbe.cs                     # 端口探测
│  ├─ BrowserOpener.cs                 # 打开浏览器
│  └─ SettingsStore.cs                 # 设置读写
├─ Tray/TrayIcon.cs                    # NotifyIcon 封装
├─ docs/
│  ├─ requirements.md
│  ├─ architecture.md
│  └─ ui-spec.md                        # 主窗口 / 托盘 UI 规格（待确认后锁定）
└─ README.md
```

关键 NuGet 依赖：无（全 BCL + WPF + Windows Forms 的 NotifyIcon）。

### D-9 图标方案（用户提供 app.ico）

- **唯一源文件**：`Assets/app.ico`，由用户提供；exe 图标、窗口标题栏图标、托盘图标三处共用同一文件。
- **csproj 引用**：
  - `<ApplicationIcon>Assets\app.ico</ApplicationIcon>` → 编译进 exe，资源管理器/任务栏显示；
  - `<Resource Include="Assets\app.ico" />` → 作为嵌入资源，运行时经 pack URI 加载。
- **运行时使用**：
  - 窗口图标：`Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"))`；
  - 托盘图标：`System.Drawing.Icon.ExtractAssociatedIcon(当前exe路径)`（与 exe 图标同源，最稳）或从嵌入资源流构造 `System.Drawing.Icon`。
- **托盘图标（已确认决策）**：恒为 app.ico 原图，**不做运行时状态色角标**；运行状态通过 NotifyIcon **tooltip 文字**（如 `Running · http://127.0.0.1:3080` / `Stopped` / `Starting…` / `Failed`）与主窗口状态条表达。
- **图标规格建议**（供用户准备文件时参考）：.ico 内嵌多分辨率 16×16 / 32×32 / 48×48 / 256×256，托盘与任务栏各尺寸都能清晰显示。若用户提供的是 PNG，可用 PowerShell + System.Drawing 转成 .ico（启动器工程内提供转换脚本）。

---

## 5. 状态机

```
                    ┌──(端口被占用/命令缺失/超时)──▶ Failed
                    │                                  │
 Stopped ──启动──▶ Starting ──(就绪行|HTTP 200)──▶ Running
    ▲                  │                               │
    └──────停止────────┴──────停止/退出───────────────┘
                    Running ──重启──▶ (停止 → 启动)
```

---

## 6. 错误处理与用户提示（FR-12）

| 场景 | 检测方式 | 提示 |
| --- | --- | --- |
| repoPath 不存在 | 启动前 `Directory.Exists` | 设置中修正路径 |
| pnpm 缺失 | `File.Exists(pnpm.cmd)` 于 PATH 探测 | 提示安装 pnpm 或改用 node 直启（自动降级） |
| Node 版本不满足 | 启动 `node --version` 解析 | 提示 DSH 要求 ^22.19 \|\| >=24 |
| 端口被其他程序占用 | `PortProbe` | 提示更换端口或关闭占用程序 |
| 前端 dist 未构建 | 就绪超时 + 日志含 "frontend dist not built" | 提示运行 `pnpm build` |
| 就绪超时 | 120 s 定时器 | 展示日志尾部，建议查看日志 |

---

## 7. 测试策略

- **单元测试**（xUnit）：`ReadinessWatcher`（正则/兜底/超时）、`PortProbe`（三种端口状态）、`SettingsStore`（读写/默认值/损坏 JSON 修复）、参数拼装（`DshProcessManager.BuildArguments` 纯函数化）；
- **集成测试**：真实 spawn `node --version` 级轻命令验证 Job Object 清理；DSH 真机冒烟（启动→就绪→浏览器→停止→无残留）；
- 服务层不依赖 UI，可直接在测试宿主中驱动。

---

## 8. 里程碑与任务分解

| 里程碑 | 任务 |
| --- | --- |
| M1 核心可用 | 脚手架、SettingsStore、DshProcessManager（spawn + Job Object + 停止）、ReadinessWatcher、BrowserOpener、最简窗口（状态 + 日志 + 启动/停止） |
| M2 体验完善 | 托盘、单实例、设置面板、端口探测防重复启动、重启、开机自启、日志落盘 |
| M3 发布 | 应用图标、单文件发布、安装说明 README、回归验收（对照 requirements.md 第 6 节） |

---

## 9. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| pnpm 不在 PATH（不同机器） | 无法启动 | 设置中可手填 pnpm 路径；自动降级 node 直启 |
| DSH 内部启动方式变化（如就绪行格式变动） | 就绪误判 | HTTP 兜底 + 就绪行正则放宽（匹配 `http://…` 即可） |
| Job Object 在某环境受限 | 孤儿进程 | fallback `taskkill /T /F` |
| .NET 8 Desktop Runtime 缺失（其他机器） | 无法运行 | 发布时选用自包含单文件（体积 ~80 MB）或引导安装运行时 |
| DSH 端口被无关程序占用 | 误判为已运行 | HTTP 响应体/状态码校验区分 DSH 与其他服务 |
