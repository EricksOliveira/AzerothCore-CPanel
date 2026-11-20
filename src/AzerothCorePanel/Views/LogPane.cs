using System.Collections.Concurrent;
using System.Text;
using System.Windows.Controls;

namespace AzerothCorePanel.Views;

/// <summary>
/// Collects log lines from any thread and writes them to a TextBox in batches, keeping the last
/// <see cref="MaxLines"/> lines so a long-running server does not grow memory without limit.
/// </summary>
public sealed class LogPane
{
    public const int MaxLines = 5000;
    private const int TrimSlack = 500;

    private readonly TextBox _box;
    private readonly ConcurrentQueue<string> _pending = new();
    private int _lineCount;

    public LogPane(TextBox box)
    {
        _box = box;
    }

    public bool AutoScroll { get; set; } = true;

    /// <summary>Thread-safe.</summary>
    public void Append(string line, bool isError)
    {
        string prefix = isError ? "!" : " ";
        _pending.Enqueue($"[{DateTime.Now:HH:mm:ss}]{prefix} {line}");
    }

    /// <summary>Must be called on the UI thread.</summary>
    public void Flush()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var text = new StringBuilder();
        while (_pending.TryDequeue(out string? line))
        {
            text.Append(line).Append('\n');
            _lineCount++;
        }

        _box.AppendText(text.ToString());

        if (_lineCount > MaxLines + TrimSlack)
        {
            Trim();
        }

        if (AutoScroll)
        {
            _box.ScrollToEnd();
        }
    }

    /// <summary>
    /// Scrolls to the newest line when auto-scroll is on. Needed after the window is shown: scrolling a
    /// hidden TextBox (panel in the tray) has no effect.
    /// </summary>
    public void ScrollToEndIfAuto()
    {
        if (AutoScroll)
        {
            _box.ScrollToEnd();
        }
    }

    public void Clear()
    {
        _pending.Clear();
        _box.Clear();
        _lineCount = 0;
    }

    private void Trim()
    {
        string current = _box.Text;
        int linesToRemove = _lineCount - MaxLines;
        int index = 0;

        for (int i = 0; i < linesToRemove && index >= 0; i++)
        {
            index = current.IndexOf('\n', index);
            if (index >= 0)
            {
                index++;
            }
        }

        if (index > 0)
        {
            _box.Text = current[index..];
            _lineCount = MaxLines;
        }
    }
}
