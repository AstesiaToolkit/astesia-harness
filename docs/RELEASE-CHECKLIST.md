# 发布清单（Release Checklist）

> 用途：每次发版前对照执行。当前版本：**v0.6.0**（上一版：v0.5.0）

---

## 1. 版本与发布前置

- [ ] 工作树干净（`git status` 无未提交改动）
- [ ] `AstesiaHarness.csproj` 的 `<Version>` 已设为本次版本号（v0.6.0）
- [ ] 本地构建通过：`dotnet build` → 0 警告 0 错误
- [ ] 所有相关提交已落地本地 main
- [ ] README / docs/TODO.md 与本版内容一致

## 2. 发布操作（手动，按顺序）

```powershell
git add -A
git commit -m "release: v0.6.0"
git push origin main
git tag v0.6.0
git push origin v0.6.0     # 触发 GitHub Actions → 自动构建双附件并创建 Release
```

- [ ] `git push origin main` 成功
- [ ] `git push origin v0.6.0` 成功（`* [new tag] v0.6.0 -> v0.6.0`）
- [ ] GitHub Actions 运行成功（Actions 页 `Release` workflow，conclusion=success）

## 3. 发布前验证清单（本机，对照功能）

| # | 验证项 | 步骤 | 预期 |
| --- | --- | --- | --- |
| 1 | 版本显示 | 启动应用 | 标题栏/设置页显示 `v0.6.0` |
| 2 | T2 打开方式-标签 | 设置→打开方式=浏览器标签页，DSH 运行中点打开浏览器 | 已开 DSH 页则切换已有标签（含最小化恢复）；多浏览器窗口也能定位 |
| 3 | T2 打开方式-应用窗口 | 打开方式=Edge 应用窗口，点打开浏览器 | 出现独立无地址栏窗口；**再次点击聚焦复用，不重复开窗** |
| 4 | T2 无 Edge 回退 | 临时改 Edge 路径/禁用（或虚拟机） | 回退默认浏览器标签页 |
| 5 | 局域网地址行 | 0.0.0.0 模式启动 | 每个地址独立一行：绿色链接点击打开该地址 + 叠纸复制按钮；URL 行复制按钮为叠纸图标 |
| 6 | 局域网过滤 | 观察状态区地址 | 不显示 Radmin VPN/Tailscale 等虚拟网卡 IP，只显示真实局域网 IP |
| 7 | T8 dsh 来源 | 设置切换 源码/全局命令 | 各自自检正确；显示对应 dsh 版本；全局模式仓库路径禁用 |
| 8 | 回归：启动/停止 | 启动→就绪→停止 | 状态流转正常，停止后无残留进程（Job/端口兜底） |
| 9 | 回归：自动更新入口 | 设置→检查更新 | 无更新提示"已是最新"；有新版本弹确认并可更新 |
| 10 | 自动更新链路（关键） | 用 v0.5.0 旧版 exe 运行 → 检查更新 | 提示 v0.6.0 → 更新 → 下载正确变体 → 校验 → 重启为 0.6.0 |

## 4. GitHub Release 发布说明（发布后粘贴到 Release 页，或改 workflow `--notes`）

```markdown
## 下载哪个？
- **AstesiaHarness.exe**（约 1.3 MB）：默认推荐，需安装 .NET 8 Desktop Runtime（https://dotnet.microsoft.com/download/dotnet/8.0/runtime）
- **AstesiaHarness-SelfContained.exe**（约 60 MB）：免依赖版，Windows 10/11 x64 双击即用

## v0.6.0 更新内容
### 新增
- **Edge 应用窗口打开方式**：设置→打开方式可选「浏览器标签页 / Edge 应用窗口」；应用窗口为独立无地址栏形态（`--app=`），已打开时自动聚焦复用不重复开窗；未安装 Edge 自动回退浏览器
- **局域网地址每地址独立行**：每个局域网地址独立一行，绿色链接点击在浏览器打开该地址 + 叠纸图标复制按钮；URL 行复制按钮统一为叠纸图标

### 改进
- 浏览器窗口枚举改为全量（EnumWindows）：多浏览器窗口场景下也能正确定位/切换已有标签页

### 其他
- dsh 来源设置（源码仓库 / 全局命令）随 v0.5.0 发布，本版延续

## 使用提示
- 首次使用请到「设置」确认 dsh 来源与仓库路径
- 局域网共享（0.0.0.0）仅在可信网络开启：SDK 会自动信任局域网 IP、无认证
- 完整性校验见同名 .sha256 文件

## 源码
- https://github.com/AstesiaToolkit/astesia-harness
- 文档：docs/（需求分析、架构设计、UI 规格、TODO）
```

## 5. 发布后检查

- [ ] Release 附件齐备：`AstesiaHarness.exe` + `.sha256`、`AstesiaHarness-SelfContained.exe` + `.sha256`（体积约 1.3MB / 60MB）
- [ ] 两个附件均可下载、SHA256 与校验文件一致
- [ ] 从 v0.5.0 旧版验证自动更新到 v0.6.0（双变体各测一次：FDD 更新到 FDD、SCD 更新到 SCD）
- [ ] 局域网共享在 0.6.0 下行为正常（补丁挂载、地址显示、防火墙规则如需要）

## 6. 注意事项 / 回滚

- **安全**：0.0.0.0 局域网共享无认证，任何局域网设备可开会话/跑工具；仅可信网络
- **回滚**：重新发布 v0.5.0（或手动把旧 exe 覆盖回去）；自动更新是"覆盖当前 exe"，回滚=运行旧版并手动替换
- **防火墙**：0.0.0.0 首次监听可能需 Windows 防火墙放行 node.exe / 端口 3080
