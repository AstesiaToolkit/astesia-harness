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
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (loaded is not null)
            {
                loaded.RepoPath = string.IsNullOrWhiteSpace(loaded.RepoPath) ? Current.RepoPath : loaded.RepoPath.Trim();
                loaded.Host = string.IsNullOrWhiteSpace(loaded.Host) ? Current.Host : loaded.Host.Trim();
                loaded.Port = loaded.Port is < 1 or > 65535 ? Current.Port : loaded.Port;
                Current = loaded;
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
