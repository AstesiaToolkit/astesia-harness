using System.Diagnostics;

namespace AstesiaHarness.Services;

/// <summary>调起系统默认浏览器打开 URL。</summary>
public static class BrowserOpener
{
    public static void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法打开浏览器：{ex.Message}", ex);
        }
    }
}
