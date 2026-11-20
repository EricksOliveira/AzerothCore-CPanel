using System.IO;

namespace AzerothCorePanel.Services;

/// <summary>
/// Finds authserver.exe and worldserver.exe inside a Build or Repack folder.
/// </summary>
public static class ServerPathDetector
{
    public const string AuthserverExe = "authserver.exe";
    public const string WorldserverExe = "worldserver.exe";

    // Common layouts: Repack (exes in the root) and CMake builds (bin/<Config>).
    private static readonly string[] KnownSubfolders =
    {
        "",
        Path.Combine("bin", "RelWithDebInfo"),
        Path.Combine("bin", "Release"),
        Path.Combine("bin", "Debug"),
        Path.Combine("bin", "MinSizeRel"),
        "bin",
    };

    public static (string? Authserver, string? Worldserver) Detect(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return (null, null);
        }

        foreach (string subfolder in KnownSubfolders)
        {
            string dir = Path.Combine(folder, subfolder);
            string auth = Path.Combine(dir, AuthserverExe);
            string world = Path.Combine(dir, WorldserverExe);

            if (File.Exists(auth) && File.Exists(world))
            {
                return (auth, world);
            }
        }

        // Fallback: search a few levels down and prefer both exes in the same folder.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
        };

        string? worldFound = Directory.EnumerateFiles(folder, WorldserverExe, options).FirstOrDefault();
        string? authFound = null;

        if (worldFound is not null)
        {
            string sibling = Path.Combine(Path.GetDirectoryName(worldFound)!, AuthserverExe);
            authFound = File.Exists(sibling) ? sibling : null;
        }

        authFound ??= Directory.EnumerateFiles(folder, AuthserverExe, options).FirstOrDefault();

        return (authFound, worldFound);
    }
}
