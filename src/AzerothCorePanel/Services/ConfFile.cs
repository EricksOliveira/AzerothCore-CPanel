using System.IO;

namespace AzerothCorePanel.Services;

/// <summary>
/// Reads AzerothCore .conf files ("Key = Value" lines, '#' comments).
/// </summary>
public static class ConfFile
{
    /// <summary>Returns the .conf next to a server exe ("configs/" first, like AzerothCore), or null.</summary>
    public static string? Find(string exePath, string confFileName)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        return new[]
            {
                Path.Combine(exeDir, "configs", confFileName),
                Path.Combine(exeDir, confFileName),
            }
            .FirstOrDefault(File.Exists);
    }

    public static Dictionary<string, string> Read(string path)
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
            // Like AzerothCore's ConfigMgr, every quote is removed ("1,4,0,\"1 9 3\"" -> "1,4,0,1 9 3").
            string value = line[(equals + 1)..].Replace("\"", string.Empty).Trim();
            values[key] = value;
        }

        return values;
    }
}
