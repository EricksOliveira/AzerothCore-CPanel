using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AzerothCorePanel.Localization;
using AzerothCorePanel.Services;
using Microsoft.Win32;

namespace AzerothCorePanel.Views;

/// <summary>
/// A server log with one colored line per message, filters by level (all, info, warnings, errors) and a text
/// search. Lines are collected from any thread and added in batches by <see cref="Flush"/>; only the last
/// <see cref="MaxLines"/> lines are kept, so a long-running server does not grow memory without limit.
/// </summary>
public partial class LogView : UserControl, INotifyPropertyChanged
{
    public const int MaxLines = 5000;
    private const int TrimSlack = 500;

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly List<LogEntry> _all = new();
    private readonly int[] _counts = new int[4];
    private readonly DispatcherTimer _searchDelay;
    private ObservableCollection<LogEntry> _visible = new();
    private LogSeverity? _filter;
    private string _search = string.Empty;
    private long _nextId;
    private ScrollViewer? _scroller;

    public LogView()
    {
        InitializeComponent();
        LinesList.ItemsSource = _visible;

        // Filtering 5000 lines on every key press would make typing slow.
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _search = SearchBox.Text.Trim();
            Rebuild();
        };

        Loaded += (_, _) =>
        {
            LinesList.ApplyTemplate();
            _scroller = LinesList.Template.FindName("Scroller", LinesList) as ScrollViewer;
            ScrollToEndIfAuto();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Base of the file name offered by "Save": "worldserver" gives "worldserver-20261122-1530.log".</summary>
    public string FileBaseName { get; set; } = "server";

    public bool AutoScroll { get; set; } = true;

    public int ErrorCount => _counts[(int)LogSeverity.Error];

    public bool HasErrors => ErrorCount > 0;

    /// <summary>Thread-safe.</summary>
    public void Append(string line, bool isError)
    {
        LogSeverity severity;
        string text;

        // The panel's own messages already say whether they are errors.
        if (line.StartsWith(L.T("Log.Prefix"), StringComparison.Ordinal))
        {
            (severity, text) = (isError ? LogSeverity.Error : LogSeverity.Info, line);
        }
        else
        {
            (severity, text) = LogClassifier.Classify(line, isError);
        }

        _pending.Enqueue(new LogEntry(Interlocked.Increment(ref _nextId), DateTime.Now, severity, text));
    }

    /// <summary>Must be called on the UI thread.</summary>
    public void Flush()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        int errorsBefore = ErrorCount;

        while (_pending.TryDequeue(out LogEntry? entry))
        {
            _all.Add(entry);
            Count(entry, 1);

            if (Matches(entry))
            {
                _visible.Add(entry);
            }
        }

        if (_all.Count > MaxLines + TrimSlack)
        {
            Trim();
        }

        UpdateCounts(errorsBefore);
        ScrollToEndIfAuto();
    }

    /// <summary>
    /// Scrolls to the newest line when auto-scroll is on. Needed after the window is shown: scrolling a
    /// hidden list (panel in the tray) has no effect.
    /// </summary>
    public void ScrollToEndIfAuto()
    {
        if (AutoScroll)
        {
            _scroller?.ScrollToEnd();
        }
    }

    public void Clear()
    {
        int errorsBefore = ErrorCount;
        _pending.Clear();
        _all.Clear();
        Array.Clear(_counts);
        _visible.Clear();
        UpdateCounts(errorsBefore);
    }

    private void Trim()
    {
        int remove = _all.Count - MaxLines;
        long lastRemovedId = _all[remove - 1].Id;

        for (int i = 0; i < remove; i++)
        {
            Count(_all[i], -1);
        }

        _all.RemoveRange(0, remove);

        // The shown lines are in the same order, so the removed ones are at their start.
        int visibleRemove = 0;
        while (visibleRemove < _visible.Count && _visible[visibleRemove].Id <= lastRemovedId)
        {
            visibleRemove++;
        }

        if (visibleRemove > _visible.Count / 2)
        {
            // Cheaper than many single removals, each of which moves the rest of the list.
            Rebuild();
            return;
        }

        for (int i = 0; i < visibleRemove; i++)
        {
            _visible.RemoveAt(0);
        }
    }

    private void Count(LogEntry entry, int delta)
    {
        if (!entry.IsBlank)
        {
            _counts[(int)entry.Severity] += delta;
        }
    }

    private bool Matches(LogEntry entry)
    {
        if (entry.IsBlank)
        {
            return _filter is null && _search.Length == 0;
        }

        bool levelMatches = _filter switch
        {
            null => true,
            LogSeverity.Info => entry.Severity is LogSeverity.Info or LogSeverity.Debug,
            LogSeverity level => entry.Severity == level,
        };

        return levelMatches
            && (_search.Length == 0 || entry.Text.Contains(_search, StringComparison.OrdinalIgnoreCase));
    }

    private void Rebuild()
    {
        _visible = new ObservableCollection<LogEntry>(_all.Where(Matches));
        LinesList.ItemsSource = _visible;

        // Wait for the new items to be laid out, or the scroll bar still has the old length.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollToEndIfAuto));
    }

    private void UpdateCounts(int errorsBefore)
    {
        AllCount.Text = FormatCount(_counts.Sum());
        InfoCount.Text = FormatCount(_counts[(int)LogSeverity.Info] + _counts[(int)LogSeverity.Debug]);
        WarningCount.Text = FormatCount(_counts[(int)LogSeverity.Warning]);
        ErrorCountText.Text = FormatCount(ErrorCount);

        if (ErrorCount != errorsBefore)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasErrors)));
        }
    }

    private static string FormatCount(int count) => count.ToString("N0", CultureInfo.CurrentCulture);

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        // Runs during InitializeComponent for the button checked in XAML, before the list exists.
        if (LinesList is null)
        {
            return;
        }

        _filter = sender == InfoFilter ? LogSeverity.Info
            : sender == WarningFilter ? LogSeverity.Warning
            : sender == ErrorFilter ? LogSeverity.Error
            : null;
        Rebuild();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void LinesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelected();
            e.Handled = true;
        }
    }

    private void CopyMenu_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void CopyAllMenu_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_visible);

    private void CopySelected() =>
        CopyToClipboard(LinesList.SelectedItems.Cast<LogEntry>().OrderBy(entry => entry.Id));

    private static void CopyToClipboard(IEnumerable<LogEntry> entries)
    {
        string text = string.Join(Environment.NewLine, entries);
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program is holding the clipboard; the user can try again.
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = L.T("Logs.Save.Title"),
            FileName = $"{FileBaseName}-{DateTime.Now:yyyyMMdd-HHmm}.log",
            DefaultExt = ".log",
            Filter = L.T("Logs.Save.Filter"),
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            File.WriteAllLines(dialog.FileName, _visible.Select(entry => entry.ToString()), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, L.T("Common.Error"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
