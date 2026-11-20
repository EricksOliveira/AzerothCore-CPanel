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
        string? confPath = new[]
            {
                Path.Combine(exeDir, "configs", confFileName),
                Path.Combine(exeDir, confFileName),
            }
            .FirstOrDefault(File.Exists);

        if (confPath is null)
        {
            return null;
        }

        Dictionary<string, string> values = ReadConf(confPath);

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

    private static Dictionary<string, string> ReadConf(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '[')
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim().Trim('"');
            values[key] = value;
        }

        return values;
    }
}
