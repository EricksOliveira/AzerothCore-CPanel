using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AzerothCorePanel.Services;

/// <summary>
/// Makes a server write the log level in front of each line ("ERROR ...", "WARN ..."), without changing the
/// .conf file: AzerothCore reads any option from an environment variable named "AC_" + the option name
/// (Appender.Console becomes AC_APPENDER_CONSOLE), so the panel starts the server with the appender's own
/// definition plus the "prefix log level" flag. Builds without that feature simply ignore the variable.
/// </summary>
public static class LogLevelPrefix
{
    // APPENDER_FLAGS_PREFIX_LOGLEVEL in src/common/Logging/LogCommon.h.
    private const int PrefixLogLevelFlag = 0x02;

    /// <summary>
    /// Adds the variable for "Appender.{appenderName}" from the given .conf; returns false when it is not needed.
    /// </summary>
    public static bool Apply(ProcessStartInfo startInfo, string confPath, string appenderName)
    {
        string key = $"Appender.{appenderName}";
        string envName = "AC_" + ToEnvVarKey(key);

        // A variable the user set themself wins.
        if (startInfo.Environment.TryGetValue(envName, out string? existing) && !string.IsNullOrEmpty(existing))
        {
            return false;
        }

        if (!ConfFile.Read(confPath).TryGetValue(key, out string? definition))
        {
            return false;
        }

        // <type>,<level>,<flags>,<optional1>,<optional2>
        List<string> parts = definition.Split(',').ToList();
        if (parts.Count < 2)
        {
            return false;
        }

        int flags = 0;
        if (parts.Count > 2
            && !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out flags))
        {
            return false;
        }

        if ((flags & PrefixLogLevelFlag) != 0)
        {
            return false;
        }

        string newFlags = (flags | PrefixLogLevelFlag).ToString(CultureInfo.InvariantCulture);
        if (parts.Count > 2)
        {
            parts[2] = newFlags;
        }
        else
        {
            parts.Add(newFlags);
        }

        startInfo.Environment[envName] = string.Join(",", parts);
        return true;
    }

    /// <summary>Same conversion as IniKeyToEnvVarKey in src/common/Configuration/Config.cpp.</summary>
    internal static string ToEnvVarKey(string key)
    {
        var result = new StringBuilder();

        for (int i = 0; i < key.Length; i++)
        {
            char curr = key[i];
            if (curr is ' ' or '.' or '-')
            {
                result.Append('_');
                continue;
            }

            if (i < key.Length - 1)
            {
                char next = key[i + 1];

                // "aB" -> "A_B" and "a1" -> "A_1"
                if ((!char.IsUpper(curr) && char.IsUpper(next)) || (!char.IsDigit(curr) && char.IsDigit(next)))
                {
                    result.Append(char.ToUpperInvariant(curr)).Append('_');
                    continue;
                }
            }

            result.Append(char.ToUpperInvariant(curr));
        }

        return result.ToString();
    }
}
