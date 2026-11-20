using System.IO;

namespace AzerothCorePanel.Services;

/// <summary>A server log file and whether the server empties it on every start (mode "w").</summary>
public sealed record LogFileInfo(string Path, bool TruncatedOnStart);

/// <summary>
/// Finds the log file written by a server's root logger by reading its .conf file
/// (LogsDir, Logger.root and the Appender.* lines).
/// </summary>
public static class LogFileLocator
{
    private const string FileAppenderType = "2";

    public static LogFileInfo? Find(string exePath, string confFileName)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        string? confPath = ConfFile.Find(exePath, confFileName);

        if (confPath is null)
        {
            return null;
        }

        Dictionary<string, string> values = ConfFile.Read(confPath);

        if (!values.TryGetValue("Logger.root", out string? rootLogger))
        {
            return null;
        }

        // Logger.root = <level>,<appender> <appender> ...
        int comma = rootLogger.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        string[] appenders = rootLogger[(comma + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        values.TryGetValue("LogsDir", out string? logsDir);
        string logsPath = string.IsNullOrWhiteSpace(logsDir) ? exeDir : Path.Combine(exeDir, logsDir);

        foreach (string appender in appenders)
        {
            // Appender.<name> = <type>,<level>,<flags>,<file>[,<mode>]
            if (!values.TryGetValue($"Appender.{appender}", out string? definition))
            {
                continue;
            }

            string[] parts = definition.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4 || parts[0] != FileAppenderType || parts[3].Length == 0)
            {
                continue;
            }

            // A "%s" in the file name is replaced by a timestamp; such files cannot be followed.
            if (parts[3].Contains('%'))
            {
                continue;
            }

            bool truncated = parts.Length > 4 && parts[4].Contains('w', StringComparison.OrdinalIgnoreCase);
            return new LogFileInfo(Path.Combine(logsPath, parts[3]), truncated);
        }

        return null;
    }
}
