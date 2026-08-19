using System.Drawing;
using System.IO;
using System.Windows;
using AstesiaHarness.ViewModels;
using Application = System.Windows.Application;
using WF = System.Windows.Forms;

namespace AstesiaHarness.Tray;

/// <summary>
/// 托盘图标：图标恒为 app.ico 原图（已确认决策：不加状态色角标），
/// 运行状态通过 tooltip 文字 + 主窗口状态条表达。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ContextMenuStrip _menu;
    private readonly MainViewModel _vm;

    public TrayIcon(MainViewModel vm)
    {
        _vm = vm;

        _menu = new WF.ContextMenuStrip();
        var openItem = _menu.Items.Add("打开浏览器");
        openItem.Click += (_, _) => _vm.RequestOpenBrowser();
        _menu.Items.Add(new WF.ToolStripSeparator());
        var startItem = _menu.Items.Add("启动");
        startItem.Click += (_, _) => _vm.RequestStart();
        var stopItem = _menu.Items.Add("停止");
        stopItem.Click += (_, _) => _vm.RequestStop();
        var restartItem = _menu.Items.Add("重启");
        restartItem.Click += (_, _) => _vm.RequestRestart();
        _menu.Items.Add(new WF.ToolStripSeparator());
        var showItem = _menu.Items.Add("显示主窗口");
        showItem.Click += (_, _) => _vm.RequestShowWindow();
        var exitItem = _menu.Items.Add("退出");
        exitItem.Click += (_, _) => _vm.RequestExit();

        _icon = new WF.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "AstesiaHarness",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) _vm.RequestShowWindow();
        };
    }

    /// <summary>更新托盘 tooltip（NotifyIcon.Text 上限 63 字符）。</summary>
    public void UpdateStatus(string text)
    {
        if (text.Length > 63) text = text[..63];
        _icon.Text = text;
    }

    /// <summary>气泡提示（开机自启等场景）。</summary>
    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(3000);
        }
        catch (Exception)
        {
            // 通知区不可用时静默降级。
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    /// <summary>
    /// 加载托盘图标：优先取嵌入资源 app.ico（16×16 帧），
    /// 失败时回退为 exe 图标（ApplicationIcon 同源）。
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (sri is not null)
            {
                using var stream = sri.Stream;
                if (stream is MemoryStream mem) return new Icon(mem, 16, 16);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                return new Icon(copy, 16, 16);
            }
        }
        catch (Exception)
        {
            // 回退到 exe 图标。
        }
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception)
        {
            // 继续回退到系统默认图标。
        }
        return SystemIcons.Application;
    }
}
