using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AstesiaHarness.Services;

/// <summary>
/// 设置存储：读写 %APPDATA%\AstesiaHarness\settings.json，管理开机自启注册表项。
/// </summary>
public sealed class SettingsStore
{
    /// <summary>应用数据目录：%APPDATA%\AstesiaHarness。</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AstesiaHarness");

    /// <summary>设置文件路径。</summary>
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>日志文件目录。</summary>
    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>T7 局域网共享补丁文件路径（Host=0.0.0.0 时生成并挂载）。</summary>
    public static string LanYmlPath => Path.Combine(DataDir, "lan.yml");

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AstesiaHarness";

    /// <summary>当前生效设置。</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>从磁盘加载设置；文件缺失用默认值，损坏时备份后回退默认值。</summary>
    public void Load()
    {
        Current = new AppSettings();
        if (!File.Exists(SettingsPath)) return;
        try
        {
            var json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded is not null)
            {
                loaded.RepoPath = string.IsNullOrWhiteSpace(loaded.RepoPath) ? Current.RepoPath : loaded.RepoPath.Trim();
                // Host 仅允许 dsh webserver schema 的两个值（127.0.0.1 / 0.0.0.0）；旧值（如 localhost）规范化回 127.0.0.1
                loaded.Host = loaded.Host is "127.0.0.1" or "0.0.0.0" ? loaded.Host : "127.0.0.1";
                loaded.Port = loaded.Port is < 1 or > 65535 ? Current.Port : loaded.Port;
                Current = loaded;
            }

            // 旧版迁移（T1）：settings.json 若含已废弃的 MinimizeToTrayOnClose 且尚无新字段，
            // 将其映射为 CloseAction（true→最小化到托盘 / false→退出程序），PromptOnClose 保持默认 false。
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("MinimizeToTrayOnClose", out var legacy)
                    && !doc.RootElement.TryGetProperty("CloseAction", out _))
                {
                    Current.CloseAction = legacy.GetBoolean() ? CloseAction.MinimizeToTray : CloseAction.Exit;
                }
            }
        }
        catch (Exception)
        {
            // 损坏的 JSON：改名备份，保留默认值。
            try { File.Move(SettingsPath, SettingsPath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}"); }
            catch (Exception) { }
            Current = new AppSettings();
        }
    }

    /// <summary>保存设置到磁盘。</summary>
    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        SyncStartWithWindows(Current.StartWithWindows);
    }

    /// <summary>恢复默认设置（不落盘，由界面"恢复默认"调用后由用户决定是否保存）。</summary>
    public void ResetToDefaults() => Current = new AppSettings();

    // ── T7 局域网共享补丁 ──────────────────────────────────────────

    /// <summary>
    /// 生成 lan.yml 补丁：经补丁层把 webserver 绑定到 0.0.0.0（绕过 dsh CLI 的 --host 0.0.0.0 守卫）。
    /// 端口保留 webStartup 表达式，始终跟随启动器设置的端口。
    /// </summary>
    public static void WriteLanPatch()
    {
        Directory.CreateDirectory(DataDir);
        const string content =
            "# 由 AstesiaHarness 自动生成：绑定 0.0.0.0 开放局域网（T7）\n" +
            "# 经补丁层绕过 dsh CLI 的 --host 0.0.0.0 守卫；端口保留表达式跟随设置\n" +
            "- id: webserver\n" +
            "  config:\n" +
            "    host: '0.0.0.0'\n" +
            "    port: !!js ctx.webStartup.port ?? 3080\n";
        File.WriteAllText(LanYmlPath, content);
    }

    /// <summary>删除 lan.yml 补丁（Host 不再是 0.0.0.0 时调用）。</summary>
    public static void RemoveLanPatch()
    {
        try { if (File.Exists(LanYmlPath)) File.Delete(LanYmlPath); } catch (Exception) { }
    }

    /// <summary>同步开机自启注册表项（HKCU Run）。</summary>
    public static void SyncStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                if (key.GetValue(RunValueName) is not null) key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // 注册表不可写时静默降级（不影响主功能）。
        }
    }
}
