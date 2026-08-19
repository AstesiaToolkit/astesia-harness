using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace AstesiaHarness.ViewModels;

/// <summary>日志区的一行（时间戳 + 内容 + 是否 stderr）。</summary>
public sealed class LogEntry
{
    public LogEntry(string text, bool isError)
    {
        TimeText = DateTime.Now.ToString("HH:mm:ss");
        Text = text;
        IsError = isError;
    }

    public string TimeText { get; }

    public string Text { get; }

    public bool IsError { get; }

    /// <summary>深色日志背景下的前景色：stderr 浅红，stdout 浅灰。</summary>
    public Brush TextBrush => IsError ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x9E)) : new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8));
}
