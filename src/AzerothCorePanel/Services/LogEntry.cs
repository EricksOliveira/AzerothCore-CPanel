namespace AzerothCorePanel.Services;

/// <summary>One line of a server log, as shown in the panel.</summary>
public sealed class LogEntry
{
    public LogEntry(long id, DateTime time, LogSeverity severity, string text)
    {
        Id = id;
        Time = time;
        Severity = severity;
        Text = text;
    }

    /// <summary>Grows with every line, so entries can be put back in arrival order.</summary>
    public long Id { get; }

    public DateTime Time { get; }

    public LogSeverity Severity { get; }

    public string Text { get; }

    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>The AzerothCore level names; not translated, like in the server's own logs.</summary>
    public string LevelText => Severity switch
    {
        LogSeverity.Debug => "DEBUG",
        LogSeverity.Warning => "WARN",
        LogSeverity.Error => "ERROR",
        _ => "INFO",
    };

    public override string ToString() => $"[{TimeText}] {LevelText,-5} {Text}";
}
