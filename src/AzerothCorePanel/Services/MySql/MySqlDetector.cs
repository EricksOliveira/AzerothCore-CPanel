using System.IO;
using Microsoft.Win32;

namespace AzerothCorePanel.Services.MySql;

/// <summary>
/// Finds the MySQL the servers use: a portable mysqld.exe inside the server folder first (common in Repacks),
/// otherwise a Windows service running mysqld or mariadbd.
/// </summary>
public static class MySqlDetector
{
    private static readonly string[] ServerExeNames = { "mysqld.exe", "mariadbd.exe" };

    public static IMySqlController? Detect(string serverFolder, Func<DatabaseInfo?> account)
    {
        return (IMySqlController?)FindPortable(serverFolder, account) ?? FindService();
    }

    private static PortableMySqlController? FindPortable(string serverFolder, Func<DatabaseInfo?> account)
    {
        if (!Directory.Exists(serverFolder))
        {
            return null;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
        };

        foreach (string exeName in ServerExeNames)
        {
            string? mysqld = Directory.EnumerateFiles(serverFolder, exeName, options).FirstOrDefault();
            if (mysqld is null)
            {
                continue;
            }

            // <mysql>\bin\mysqld.exe: look for the config in <mysql>, then in the server folder.
            string baseDir = Path.GetDirectoryName(Path.GetDirectoryName(mysqld)!)!;
            string? defaultsFile = new[]
                {
                    Path.Combine(baseDir, "my.ini"),
                    Path.Combine(baseDir, "my.cnf"),
                    Path.Combine(serverFolder, "my.ini"),
                }
                .FirstOrDefault(File.Exists);

            return new PortableMySqlController(mysqld, defaultsFile, account);
        }

        return null;
    }

    private static ServiceMySqlController? FindService()
    {
        var candidates = new List<string>();

        try
        {
            using RegistryKey? services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null)
            {
                return null;
            }

            foreach (string name in services.GetSubKeyNames())
            {
                using RegistryKey? service = services.OpenSubKey(name);
                if (service?.GetValue("ImagePath") is string imagePath
                    && ServerExeNames.Any(exe => imagePath.Contains(exe, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(name);
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        // With several installs, prefer the one that is running.
        string? chosen = candidates.FirstOrDefault(name => WindowsService.GetState(name) == ServiceState.Running)
            ?? candidates.FirstOrDefault();

        return chosen is null ? null : new ServiceMySqlController(chosen);
    }
}
