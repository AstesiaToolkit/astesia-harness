using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AstesiaHarness.Services;

/// <summary>环境自检发现的问题：指引文字 + 可选链接。</summary>
public sealed record EnvIssue(string Message, string? LinkUrl = null, string? LinkText = null);

/// <summary>
/// 环境自检（T3）：启动前集中检查 仓库路径 / Node 存在 / Node 版本，返回首个阻断性问题及指引；
/// 同时提供 PATH 查找与 Node 版本校验工具（DshProcessManager 复用，避免重复实现）。
/// </summary>
public static class EnvironmentCheck
{
    public const string NodeDownloadUrl = "https://nodejs.org";

    /// <summary>在 PATH 中查找可执行文件（node.exe / pnpm.cmd / …），返回完整路径或 null。</summary>
    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { }
        }
        return null;
    }

    public static string? FindNodePath() => FindOnPath("node.exe");

    public static string? FindPnpmPath() => FindOnPath("pnpm.cmd") ?? FindOnPath("pnpm.exe") ?? FindOnPath("pnpm");

    /// <summary>DSH engines 要求：^22.19.0 || >=24.0.0。</summary>
    public static async Task<bool> IsNodeVersionSupportedAsync(string nodePath)
    {
        var version = await TryGetNodeVersionAsync(nodePath);
        if (version is null) return false;
        var match = Regex.Match(version, @"v?(\d+)\.(\d+)");
        if (!match.Success) return false;
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        return major >= 24 || (major == 22 && minor >= 19) || major > 22;
    }

    public static async Task<string?> TryGetNodeVersionAsync(string nodePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>前端构建产物是否存在（apps/web/dist/index.html），仅作提示。</summary>
    public static bool HasFrontendDist(string repoPath) =>
        File.Exists(Path.Combine(repoPath, "apps", "web", "dist", "index.html"));

    /// <summary>
    /// 启动前集中自检，返回首个阻断性问题（null = 全部通过）。
    /// 顺序：仓库路径 → Node 存在 → Node 版本（pnpm 缺失可降级 node 直启，不阻断）。
    /// </summary>
    public static async Task<EnvIssue?> CheckAsync(AppSettings settings)
    {
        if (!Directory.Exists(settings.RepoPath))
        {
            return new EnvIssue(
                $"仓库路径不存在：{settings.RepoPath}。请到「设置」页修改为正确的 DeepSeek Harness 仓库目录。");
        }

        var nodePath = FindNodePath();
        if (nodePath is null)
        {
            return new EnvIssue(
                "未检测到 Node.js。请下载安装（DSH 要求 ^22.19 或 >=24），安装后重启启动器。",
                NodeDownloadUrl, "打开 Node.js 下载页");
        }

        if (!await IsNodeVersionSupportedAsync(nodePath))
        {
            var version = await TryGetNodeVersionAsync(nodePath) ?? "未知";
            return new EnvIssue(
                $"当前 Node {version} 不满足 DSH 要求（^22.19.0 或 >=24.0.0），请升级 Node.js。",
                NodeDownloadUrl, "打开 Node.js 下载页");
        }

        return null;
    }
}
