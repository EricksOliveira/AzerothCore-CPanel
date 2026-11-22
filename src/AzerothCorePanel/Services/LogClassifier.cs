using System.Text.RegularExpressions;

namespace AzerothCorePanel.Services;

/// <summary>Severity of one log line, from AzerothCore's log levels.</summary>
public enum LogSeverity
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Finds the level of a server log line. Lines carry the level when the appender has the
/// "prefix log level" flag (see <see cref="LogLevelPrefix"/>); otherwise the stream (errors go to stderr)
/// and a few keywords decide.
/// </summary>
public static partial class LogClassifier
{
    // Optional "AC>" console prompt, optional "2026-11-22_10:00:00 " timestamp, then the level and a space.
    [GeneratedRegex(
        @"^(?:AC>\s*)?(?<time>\d{4}-\d{2}-\d{2}_\d{2}:\d{2}:\d{2} )?(?<level>FATAL|ERROR|WARN|INFO|DEBUG|TRACE) ")]
    private static partial Regex LevelPrefix();

    [GeneratedRegex(@"\b(ERROR|FATAL|CRASH)\b|\b(Error|Fatal|Exception):|\bfailed\b", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorWords();

    [GeneratedRegex(@"\bWARN(ING)?\b|\bWarning\b", RegexOptions.CultureInvariant)]
    private static partial Regex WarningWords();

    /// <summary>Returns the level and the line without its level prefix.</summary>
    public static (LogSeverity Severity, string Text) Classify(string line, bool fromErrorStream)
    {
        Match match = LevelPrefix().Match(line);
        if (match.Success)
        {
            LogSeverity severity = match.Groups["level"].Value switch
            {
                "FATAL" or "ERROR" => LogSeverity.Error,
                "WARN" => LogSeverity.Warning,
                "INFO" => LogSeverity.Info,
                _ => LogSeverity.Debug,
            };

            return (severity, match.Groups["time"].Value + line[match.Length..]);
        }

        if (fromErrorStream || ErrorWords().IsMatch(line))
        {
            return (LogSeverity.Error, line);
        }

        return (WarningWords().IsMatch(line) ? LogSeverity.Warning : LogSeverity.Info, line);
    }
}
