# AstesiaHarness

DeepSeek Harness Web GUI 快速启动器（Windows 桌面软件）。

一键后台启动 `pnpm dsh web`，自动等待服务就绪并打开浏览器；托盘常驻，支持一键停止/重启、日志查看、设置持久化。

## 功能

- **一键启动**：后台拉起 DSH Web 服务（无控制台窗口），自动检测就绪（解析官方就绪行 + HTTP 轮询兜底）并打开浏览器；
- **一键停止 / 重启**：基于 Windows Job Object 终结整条进程树（pnpm → node → esbuild），无孤儿进程；
- **已运行检测**：端口已被 DSH Web 占用时直接打开浏览器，不重复启动；被其他程序占用时明确提示；
- **智能打开浏览器**：DSH 运行中按钮始终可用；若 Chrome/Edge 已打开 DSH 页面则切换并聚焦已有标签页（含最小化/后台窗口自动恢复），否则新开标签页；
- **关闭行为可选**：「关闭主界面时」可设为 退出程序 / 最小化到托盘（互斥），并可开启「退出时提示」——每次点 × 询问，勾选「不再提示」后写回设置持久记住；
- **自动更新**：设置页显示版本号、可手动「检查更新」，支持启动时静默检查；发现新版本从 GitHub Releases 下载**对应变体**（框架依赖/自包含各自匹配），SHA256 校验后覆盖旧 exe 并自动重启；标题栏显示当前版本，有更新时绿色高亮徽标可点击直达更新；
- **打开软件时同时启动 dsh**：可选设置，开启后应用启动即自动拉起服务（可与开机自启组合）；
- **环境自检引导**：启动前自动检查仓库路径 / Node.js 存在与版本，失败时给出具体指引（含 Node 下载链接）；
- **托盘常驻**：状态 tooltip + 右键菜单（打开浏览器 / 启动 / 停止 / 重启 / 显示主窗口 / 退出）；
- **运行/设置双 Tab**：日志实时滚动（stdout/stderr 着色）、设置项（仓库路径 / 端口 / 主机 / 附加参数 / 自动开浏览器 / 关闭行为 / 开机自启）；
- **单实例**：重复启动自动聚焦已有窗口（二次实例优雅退出，不崩溃）。

## 快速开始

### 前置条件

- Windows 10/11；
- .NET 8 SDK（开发）或 .NET 8 Desktop Runtime（运行）；
- DeepSeek Harness 仓库已 `pnpm install`，且 pnpm（或 node）在 PATH 中。

### 构建与运行

```powershell
# 开发调试
dotnet build AstesiaHarness.csproj
dotnet run --project AstesiaHarness.csproj

# 发布单文件（框架依赖，产物 ~1 MB，目标机器需 .NET 8 Desktop Runtime）
dotnet publish AstesiaHarness.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
# 发布自包含单文件（产物 ~80 MB，目标机器无需安装运行时）
dotnet publish AstesiaHarness.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

### 使用

1. 打开主窗口，在「设置」页确认 DSH 仓库路径与端口（默认 `F:\Codes\DownloadProjects\deepseek-harness` / `3080`），点「保存设置」；
2. 回「运行」页点「▶ 启动」；
3. 状态转「运行中」后自动打开 `http://127.0.0.1:3080`；
4. 关闭窗口即最小化到托盘；托盘右键「退出」结束。

## 图标

- 图标源文件：`Assets/app.ico`（exe / 窗口 / 托盘三处共用），替换后重新构建即可生效；
- 规格建议：内嵌多分辨率 16/32/48/256 px，详见 `Assets/README.md`。

## 文档

- [需求分析](docs/requirements.md)
- [架构设计与技术选型](docs/architecture.md)
- [主窗口 UI 规格（已确认基线）](docs/ui-spec.md)

## 工程结构

```
AstesiaHarness/
├─ AstesiaHarness.csproj
├─ App.xaml / App.xaml.cs          # 入口、单实例守卫、托盘生命周期
├─ MainWindow.xaml / .cs           # 运行 / 设置双 Tab 主窗口
├─ Services/                       # 纯逻辑服务层（不依赖 UI）
│  ├─ DshProcessManager.cs         # 进程生命周期 + Job Object 进程树管理
│  ├─ ReadinessWatcher.cs          # 就绪判定（stdout 就绪行 + HTTP 轮询）
│  ├─ PortProbe.cs                 # 端口探测（已运行检测）
│  ├─ BrowserOpener.cs             # 调起默认浏览器
│  ├─ SettingsStore.cs             # settings.json 读写 + 开机自启注册表
│  ├─ AppSettings.cs / ServerState.cs
├─ ViewModels/                     # MainViewModel / RelayCommand / LogEntry
├─ Tray/TrayIcon.cs                # NotifyIcon 封装
└─ Assets/app.ico                  # 主图标
```

## 发布新版本（GitHub Actions 自动发布）

推送格式为 `v*` 的版本 tag 即自动触发 CI，构建单文件 exe 并发布 GitHub Release：

```powershell
git tag v0.1.1
git push origin v0.1.1
```

- Workflow：`.github/workflows/release.yml`（构建单文件 exe + SHA256 校验和 → `gh release create`）；
- 产物下载：https://github.com/AstesiaToolkit/astesia-harness/releases ，每次发布两个附件：
  - `AstesiaHarness.exe`（约 1.3 MB）—— 框架依赖，需 .NET 8 Desktop Runtime（[下载运行时](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)）；
  - `AstesiaHarness-SelfContained.exe`（约 60 MB）—— 自包含免依赖，Windows 10/11 x64 双击即用；
- 版本号取自 tag（如 `v0.1.1` → 程序集版本 `0.1.1`）。

> 体积说明：自包含版捆绑了整个 .NET 运行时所以偏大；已通过开启单文件压缩、去除 ICU 全球化数据、裁剪非中英语言包从 147 MB 优化到约 60 MB。若目标机可安装运行时，请优先使用 1.3 MB 的框架依赖版。

## 数据位置

- 设置：`%APPDATA%\AstesiaHarness\settings.json`
- 日志：`%APPDATA%\AstesiaHarness\logs\`（预留）
