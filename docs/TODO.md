# AstesiaHarness TODO（待实现功能清单）

> 状态说明：✅ = 已实现；⬜ = 待实现。每项含需求、交互设计、涉及文件与验收标准，实现时直接按此执行。
> 当前版本：v0.2.0

---

## ✅ T1. 关闭主界面行为设置（退出程序 / 最小化到托盘 互斥 + 退出时提示）— 已实现于 v0.2.0

### 需求

新增设置项「关闭主界面时」，让用户明确选择点窗口 ✕ 后的行为：

- **互斥单选**：`退出程序` | `最小化到托盘`
- **独立复选**：`退出时提示`

### 交互设计

**情形 A：未勾选「退出时提示」**
点 ✕ 直接按设置中的互斥选项执行（退出程序 / 最小化到托盘）。

**情形 B：勾选「退出时提示」**
点 ✕ 弹出对话框：

1. 互斥单选：`退出程序` / `最小化到托盘`（默认选中设置中的当前选项）；
2. 独立复选：`不再提示`；
3. 按钮：`取消` / `确定`。

- **取消** → 不关闭主界面，什么都不改；
- **确定** → 按所选选项执行：
  - 选 `最小化到托盘` → 隐藏窗口；
  - 选 `退出程序` → 退出程序（若服务在运行，沿用现有"停止服务并退出 / 保持运行并退出 / 取消"二次确认）；
  - **`不再提示` 语义（已明确：写回设置，持久生效）**：勾选「不再提示」并点「确定」时，本次选择**立即写回设置并保存 settings.json**：
    1. `CloseAction` ← 本次所选（`退出程序` 或 `最小化到托盘`）；
    2. `PromptOnClose` ← `false`（关闭「退出时提示」开关）；
    3. 此后点 ✕ **不再弹窗**，直接按已写回的 `CloseAction` 执行。
    未勾选「不再提示」→ 本次选择**仅本次生效、不写回**：下次点 ✕ 仍弹窗，且对话框默认值仍为设置值。

### 设置项变更

| 旧字段 | 新字段 | 说明 |
| --- | --- | --- |
| `MinimizeToTrayOnClose` (bool) | `CloseAction` (enum: `MinimizeToTray` \| `Exit`) + `PromptOnClose` (bool) | settings.json 需向后兼容：读取旧 bool 时迁移为 `CloseAction`，默认 `MinimizeToTray` |

### 涉及文件

- `Services/AppSettings.cs`（枚举 + 字段迁移）
- `ViewModels/MainViewModel.cs`（关闭行为逻辑、退出确认流程复用）
- `MainWindow.xaml(.cs)`（Closing 处理改走新逻辑、弹窗）
- 新增 `ViewModels/ClosePromptDialog`（或等价自绘对话框：互斥单选 + 不再提示复选 + 取消/确定）

### 验收标准

1. 设置「退出程序」+ 未勾提示 → 点 ✕ 直接退出；
2. 设置「最小化到托盘」+ 未勾提示 → 点 ✕ 最小化到托盘（现状行为）；
3. 勾「退出时提示」→ 点 ✕ 弹对话框，取消不生效、确定按所选执行；
4. 勾「不再提示」并确定 → `CloseAction` 与 `PromptOnClose` 均被写回 settings.json（持久），下次点 ✕ 不再弹窗；
5. 未勾「不再提示」→ 本次选择仅本次生效，设置值不变，下次点 ✕ 仍弹窗；
6. 退出时服务在跑 → 沿用"停止并退出 / 保持运行并退出 / 取消"确认；
7. 旧版 settings.json（含 `MinimizeToTrayOnClose`）升级后行为正确。

> 已确认：`不再提示` 采用**写回设置（持久记住）**语义，见上方交互设计；不提供"仅本次"选项。

---

## ⬜ T2. Edge --app= 应用模式打开选项（待实现）

### 需求

设置项「打开方式」新增第二种形态：**Edge 应用窗口**（`--app=` 模式），与现有"浏览器标签"互斥可选。

### 交互设计

- 设置页新增「打开方式」互斥单选：`浏览器标签页`（默认，现状行为）/ `Edge 应用窗口`；
- 选择 `Edge 应用窗口` 后，点「打开浏览器」（或自动打开）时，**按以下优先级执行（防重复窗口）**：
  1. **已安装 PWA 优先**：若用户已把 DSH 站点安装为应用（`Get-StartApps` 匹配 Harness 且 AppID 以 `!App` 结尾）→ 用 `explorer.exe shell:AppsFolder\<AppID>` 启动。系统应用模型**天然单实例**：已开窗口时自动聚焦，不会重复；
  2. **已有应用窗口复用**：无 PWA 时，先用 **EnumWindows 全量枚举** msedge 顶层窗口，判定"应用模式窗口"（窗口标题含 `DeepSeek Harness`/`host:port` **且 UIA 树无 Tab 控件**——应用窗口无标签栏，以此与普通浏览器窗口区分）→ 存在则 `SW_RESTORE` + `SetForegroundWindow` 聚焦，不新开；
  3. **新开应用窗口**：未找到 → 定位 Edge（`%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe` → `%ProgramFiles%...`）→ `msedge.exe --app=<url> --profile-directory=Default`；
  4. **Edge 缺失** → 回退默认浏览器标签页，并提示。
- **附带修正**：现有 `OpenOrFocus` 用 `Process.MainWindowHandle` 每进程只能取到**一个**窗口（浏览器多窗口时可能漏查）→ 统一改为 EnumWindows 全量枚举，使"浏览器标签"模式在多窗口场景也能正确定位（本项一并处理）。
- 应用模式与 UIA 标签切换互斥：选应用模式时不走 `OpenOrFocus` 的标签切换逻辑。

### 涉及文件

- `Services/BrowserOpener.cs`（新增 `OpenAppMode(url)`、Edge 路径探测、EnumWindows 窗口枚举重构）
- `ViewModels/MainViewModel.cs`（打开方式路由）
- `Services/AppSettings.cs`（新增 `OpenMode` 字段：`BrowserTab` | `EdgeApp`，默认 `BrowserTab`）
- `MainWindow.xaml`（设置页新增单选组）

### 验收标准

1. 选「浏览器标签页」→ 行为与现在完全一致（含 UIA 切换已有标签）；**多浏览器窗口时也能定位到正确窗口**；
2. 选「Edge 应用窗口」→ 以独立应用窗口打开，无地址栏标签页；
3. **重复点击 / 已存在应用窗口 → 聚焦已有窗口，不重复新开**（覆盖 PWA 单实例与 --app 探测两条路径）；
4. 机器无 Edge → 回退默认浏览器并给出提示。

### 待确认点

- `--app=<url>` 重复启动的默认行为（新开 vs 聚焦）随 Edge 版本而异，实现时先实测；无论结果如何都按"先探测复用、再新开"流程保证不重复；
- 应用模式窗口判定以"UIA 无 Tab 控件 + 标题匹配"为准；若个别 Edge 版本仍暴露 Tab 控件导致误判，回退为"标题匹配 + 无『和另外 N 个页面』后缀"启发式。

---

## ✅ T3. 环境自检引导（文字提示）— 已实现于 v0.2.0

### 需求

启动失败时给出**具体、可操作的文字指引**（含下载链接与命令），替代现在偏笼统的报错。

### 自检项与指引文案（启动前集中检查 + 启动中兜底）

| 场景 | 检测方式 | 指引文字（示例） |
| --- | --- | --- |
| node 未找到 | `node --version` 失败 | "未检测到 Node.js。请从 https://nodejs.org 下载安装（DSH 要求 ^22.19 或 >=24），安装后重启启动器。" |
| node 版本不满足 | 解析版本号 | "当前 Node vX.Y.Z 不满足 DSH 要求（^22.19.0 或 >=24.0.0），请升级 Node.js。" |
| pnpm 未找到且无 node 直启可能 | PATH 探测 pnpm.cmd | "未找到 pnpm，且无法降级到 node 直启。请安装 pnpm：`npm install -g pnpm`（或设置中指定 pnpm 路径）。" |
| 仓库路径不存在 | `Directory.Exists` | "仓库路径不存在：<path>。请到「设置」页修改为正确的 DSH 仓库目录。" |
| 前端 dist 未构建 | 就绪超时 + 日志含 "frontend dist not built" | "DSH 前端未构建。请在该仓库运行 `pnpm build` 后重试。" |
| 端口被占用 | `PortProbe` | "端口 <port> 已被其他程序占用（非 DSH Web）。请关闭占用程序，或在设置中更换端口。" |

### 交互设计

- 启动前执行集中自检，失败即 `Failed` 状态 + 消息条显示指引 + 日志记录；
- 指引为**文字 + 可点击链接**（如 Node.js 下载页），消息条支持超链接；
- 自检通过后仍按现有流程启动（运行时错误沿用现有 Error 提示并尽量映射到上表文案）。

### 涉及文件

- `Services/DshProcessManager.cs`（自检逻辑集中化、错误分类）
- 新增 `Services/EnvironmentCheck.cs`（node/pnpm/仓库/dist 检测，纯函数可单测）
- `ViewModels/MainViewModel.cs`（指引消息渲染、链接打开）
- `MainWindow.xaml`（消息条支持链接）

### 验收标准

1. 分别模拟 6 种故障（删 node、降版本、删 pnpm、改错路径、删 dist、占端口），每种都显示对应指引文字与链接；
2. 正常环境自检通过，启动流程不受影响；
3. 指引链接可点击打开。

---

## ⬜ T4. 自动更新（版本检查 + 下载覆盖）— 待实现

### 需求

1. 设置页显示**当前版本号**，并提供「检查更新」入口；
2. 设置项「自动检查更新」（启动时后台静默检查）；
3. 发现新版本 → 从 GitHub Releases 下载**与当前变体匹配**的最新版，校验后**覆盖旧 exe 并重启**。

### ⚠️ 前置条件（重要）

**当前仓库为私有**：GitHub Releases API（`/releases/latest`）与资产下载对未认证请求返回 404。自动更新只对能访问该仓库的人有效。**若要对普通用户生效，需将仓库设为公开**（或另配只读 Token——不建议把 Token 打进程序）。此点在实现前需与用户确认仓库可见性。

### 实现方案

1. **版本来源与比较**
   - 本地版本：`AssemblyVersion`（csproj `<Version>`，如 0.2.0）；
   - 远端版本：`GET https://api.github.com/repos/AstesiaToolkit/astesia-harness/releases/latest` → `tag_name`（vX.Y.Z，`/latest` 自动跳过预发布）；
   - 语义化比较 major.minor.patch（整数逐段比较），忽略 `v` 前缀。

2. **变体匹配（FDD 1.3MB / SCD 60MB 双资产）**
   - 发布两个变体时由 workflow 传入 `-p:UpdateAsset=AstesiaHarness.exe`（FDD）/ `-p:UpdateAsset=AstesiaHarness-SelfContained.exe`（SCD）；
   - csproj：`<AssemblyMetadata Include="UpdateAsset" Value="$(UpdateAsset)" />` 编译进程序集；
   - 运行时读 `AssemblyMetadata("UpdateAsset")` 决定下载哪个资产（默认 `AstesiaHarness.exe`）。

3. **下载与校验**
   - `HttpClient` 下载资产到 **exe 同目录** `AstesiaHarness.exe.update`（可显示进度百分比）；
   - 同时下载对应 `.sha256`，用 `Get-FileHash` 比对；**不一致则中止**，不替换。

4. **替换与重启（Windows 自更新经典模式，单文件 exe 只需替换自身）**
   - 预检 exe 目录可写（尝试在该目录创建临时文件）；不可写 → 提示"请将程序移至可写目录或以管理员运行"；
   - 生成隐藏 updater 批处理（写临时 .cmd，`Process.Start` WindowStyle Hidden）：
     `等待主进程退出（约 2s）→ move /y "AstesiaHarness.exe.update" "AstesiaHarness.exe" → start "" "AstesiaHarness.exe"`；
   - 主程序确认后 `Application.Shutdown()`（服务在跑时沿用"停止并退出/保持运行并退出/取消"确认）；
   - 更新流程结束由新进程接管，旧进程不残留。

5. **UI**
   - 设置页新增「版本与更新」区：当前版本号 + 自动检查更新开关 + 检查更新按钮 + 下载进度；
   - 检查结果：无更新 → "已是最新版本"；有更新 → 弹窗（当前 vX → 新 vY）确认后下载；
   - 与 T5 联动：发现新版本时主窗口标题位置高亮提示。

### 涉及文件

- 新增 `Services/UpdateService.cs`（版本查询/比较/下载/校验/暂存替换，纯逻辑可单测）
- `Services/AppSettings.cs`（`AutoCheckUpdate`）
- `ViewModels/MainViewModel.cs`（更新命令、状态、进度）
- `MainWindow.xaml`（设置页「版本与更新」区）
- `.github/workflows/release.yml`（两个 publish 各传 `UpdateAsset`）
- `AstesiaHarness.csproj`（AssemblyMetadata）

### 验收标准

1. 设置页显示当前版本；
2. 手动检查：无更新提示"已是最新"；有更新弹窗确认；
3. 确认后下载正确变体资产，SHA256 校验通过 → 程序退出 → exe 被替换 → 新版本自动启动；
4. 校验失败 → 中止并提示，不替换原文件；
5. exe 目录不可写 → 明确提示，不执行替换；
6. 「自动检查更新」开启时启动后台静默检查，有更新仅提示不自动下载；
7. 网络失败/离线 → 静默或提示网络错误，不影响正常使用。

### 待确认点

- 「自动检查更新」默认值（建议开启）；
- 更新弹窗是否展示 Release notes（可从 GitHub API 取 body，后续可加）；
- 仓库可见性：是否转公开（否则更新仅对仓库可访问者生效）。

---

## ⬜ T5. 主窗口 Title 位置版本显示 + 更新高亮点击 — 待实现

### 需求

- 主窗口 **Title 位置**显示版本号；
- 检测到更新时该处**高亮显示**，且**可点击**触发 T4 更新流程。

### 实现方案（二选一，需确认）

- **方案 A（推荐，贴合"Title 位置"）：WindowChrome 自定义标题栏**
  - 用 `WindowChrome` 替换原生标题栏：左侧图标 + 应用名 + **版本号**；右上角**更新徽标**（有更新时高亮变色、可点击触发更新）与最小化/关闭按钮（自绘，保留拖拽区、双击最大化）；
  - 版本/更新徽标是真正的可交互元素，语义最贴合需求；
  - 成本：标题栏自绘约 60~100 行 XAML + 行为，需处理系统按钮/拖拽/DPI。
- **方案 B（轻量备选）：原生标题 + 顶部状态栏版本块**
  - 窗口 `Title` 字符串附加版本（任务栏可见，如 `AstesiaHarness v0.2.0 — …`）；
  - 状态条右上角加版本文本块：有更新时绿色高亮 + 下划线，点击触发更新；
  - 改动小、风险低，但版本不在原生标题栏内。

### 涉及文件

- `MainWindow.xaml(.cs)`（WindowChrome 或版本块）
- `ViewModels/MainViewModel.cs`（`UpdateAvailable` / 版本显示 / `CheckUpdateCommand` / `UpdateNowCommand`）
- 与 T4 的 `UpdateService` 联动

### 验收标准

1. 标题位置显示版本号；
2. 无更新：常规样式；有更新：高亮 + 可点击；
3. 点击进入 T4 的确认/下载流程。

### 待确认点

- 方案 A（自定义标题栏）还是方案 B（状态栏版本块）？

---

## ⬜ T6. 设置「打开软件时同时启动 dsh」— 待实现

### 需求

新增设置项：**打开软件时同时启动 dsh**；开启后应用启动即自动拉起服务。

### 实现方案

1. `AppSettings.AutoStartServerOnLaunch`（bool，默认 **false**，保持现状）；
2. 设置页勾选框（「打开软件时」区或启动相关区）；
3. 应用启动流程（`App.OnStartup` 创建 VM/manager 后）：若开启 → `manager.StartAsync()`（异步，不阻塞窗口显示；失败走 Failed 状态 + 现有指引提示）；
4. 组合行为：
   - 与「开机自启（--minimized）」组合 → 登录后托盘运行 + 服务自动就绪；
   - 与「自动打开浏览器」组合 → 就绪后按现有 `AutoOpenBrowser` 行为执行（待确认：登录自启场景是否抑制自动开浏览器，避免登录时弹浏览器）。

### 涉及文件

- `Services/AppSettings.cs`（新字段）
- `App.xaml.cs` 或 `ViewModels/MainViewModel.cs`（启动时触发）
- `MainWindow.xaml`（勾选框）

### 验收标准

1. 勾选保存后重启应用 → 自动 Starting → Running（无需手点启动）；
2. 不勾选 → 行为与现在完全一致；
3. 自动启动失败 → Failed 状态 + 指引可见，不影响应用本身。

### 待确认点

- 默认值 false（保守）是否可接受；
- 开机自启场景是否抑制自动打开浏览器。

---

## 实现进度

- ✅ T1（关闭行为设置）— 已实现于 v0.2.0
- ⬜ T2（Edge 应用模式打开）— 待实现（无依赖，可随时开工）
- ✅ T3（环境自检引导）— 已实现于 v0.2.0
- ⬜ T4（自动更新）— 待实现（有仓库可见性前置条件）
- ⬜ T5（标题版本显示 + 更新高亮点击）— 待实现（依赖 T4）
- ⬜ T6（打开软件时同时启动 dsh）— 待实现
