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

## 实现进度

- ✅ T1（关闭行为设置）— 已实现于 v0.2.0
- ⬜ T2（Edge 应用模式打开）— 待实现（无依赖，可随时开工）
- ✅ T3（环境自检引导）— 已实现于 v0.2.0
