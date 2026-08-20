using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http;

namespace AstesiaHarness.Services;

/// <summary>检查更新结果。</summary>
public sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string AssetName,
    string? AssetUrl,
    string? AssetSha256Url,
    bool UpdateAvailable,
    string? ReleaseNotes);

/// <summary>
/// 自动更新（T4）：查询 GitHub Releases 最新版、按变体下载资产、SHA256 校验、
/// 暂存替换并重启。当前版本取自 AssemblyVersion，资产名取自 AssemblyMetadata("UpdateAsset")。
/// </summary>
public static class UpdateService
{
    public const string Repo = "AstesiaToolkit/astesia-harness";
    private const string ApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>当前程序版本（如 "0.3.0"）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>本次构建应更新的发布资产名（FDD → AstesiaHarness.exe；SCD → AstesiaHarness-SelfContained.exe）。</summary>
    public static string UpdateAssetName
    {
        get
        {
            var attr = typeof(UpdateService).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, "UpdateAsset", StringComparison.OrdinalIgnoreCase));
            return attr?.Value ?? "AstesiaHarness.exe";
        }
    }

    /// <summary>
    /// 查询最新发布。网络/仓库不可达返回 null；否则返回 <see cref="UpdateInfo"/>（可能无更新）。
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AstesiaHarness/{CurrentVersion}");
            using var resp = await http.GetAsync(ApiUrl, cancellationToken);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            string? assetUrl = null;
            string? sha256Url = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.Equals(name, UpdateAssetName, StringComparison.OrdinalIgnoreCase)) assetUrl = url;
                    else if (string.Equals(name, UpdateAssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) sha256Url = url;
                }
            }

            var latest = ParseVersion(tag);
            var current = ParseVersion("v" + CurrentVersion);
            var available = latest is not null && (current is null || latest > current);

            return new UpdateInfo(CurrentVersion, tag.TrimStart('v').TrimStart('V'), UpdateAssetName, assetUrl, sha256Url, available, notes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>下载资产到指定路径，可上报进度（0..1）。</summary>
    public static async Task DownloadAsync(string url, string destPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AstesiaHarness/{CurrentVersion}");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(cancellationToken);
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long read = 0;
        while (true)
        {
            var n = await src.ReadAsync(buffer, cancellationToken);
            if (n <= 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
    }

    /// <summary>校验文件 SHA256 与发布方一致（.sha256 资产内容形如 "SHA256 &lt;hex&gt;  name"）。</summary>
    public static async Task<bool> VerifySha256Async(string filePath, string? sha256Url, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(sha256Url)) return false;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AstesiaHarness/{CurrentVersion}");
            var content = (await http.GetStringAsync(sha256Url, cancellationToken)).Trim();
            var match = Regex.Match(content, @"[0-9A-Fa-f]{64}");
            if (!match.Success) return false;

            await using var file = File.OpenRead(filePath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            return string.Equals(match.Value, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>程序所在目录是否可写（更新替换的前提）。</summary>
    public static bool CanWriteExeDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir)) return false;
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 生成隐藏 updater 并启动：等待本进程退出（最多约 30s）→ 用 .update 覆盖自身 → 重启新版本。
    /// 调用后调用方应立即退出（先处理服务停止/脱离）。
    /// </summary>
    public static void LaunchUpdaterAndExit()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序路径");
        var dir = Path.GetDirectoryName(exe) ?? throw new InvalidOperationException("无法定位当前程序目录");
        var exeName = Path.GetFileName(exe);
        var newFile = Path.Combine(dir, exeName + ".update");
        var script = Path.Combine(Path.GetTempPath(), $"AstesiaHarness-update-{Guid.NewGuid():N}.cmd");

        var lines = new[]
        {
            "@echo off",
            "setlocal enabledelayedexpansion",
            $"set \"OLD={exe}\"",
            $"set \"NEW={newFile}\"",
            "set /a tries=0",
            ":wait",
            $"tasklist /FI \"IMAGENAME eq {exeName}\" 2>nul | find /i \"{exeName}\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  set /a tries+=1",
            "  if !tries! lss 30 goto wait",
            ")",
            "move /y \"%NEW%\" \"%OLD%\" >nul 2>&1",
            "start \"\" \"%OLD%\"",
        };
        File.WriteAllLines(script, lines);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = true,
        });
    }

    /// <summary>解析 "vX.Y.Z" → Version（失败返回 null）。</summary>
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var v) ? v : null;
    }
}
