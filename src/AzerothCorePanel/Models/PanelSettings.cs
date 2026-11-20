using System.IO;
using System.Text.Json;

namespace AzerothCorePanel.Models;

/// <summary>
/// Panel settings, saved as JSON in %APPDATA%\AzerothCorePanel\settings.json.
/// </summary>
public sealed class PanelSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ServerFolder { get; set; } = string.Empty;
    public string AuthserverPath { get; set; } = string.Empty;
    public string WorldserverPath { get; set; } = string.Empty;

    /// <summary>
    /// How long to wait for a graceful worldserver shutdown before killing the process.
    /// </summary>
    public int ShutdownTimeoutSeconds { get; set; } = 60;

    /// <summary>Restart a server that closed without being asked to (crash).</summary>
    public bool AutoRestartOnCrash { get; set; } = true;

    public int CrashRestartDelaySeconds { get; set; } = 5;

    /// <summary>Give up after this many crash restarts within <see cref="CrashRestartWindowMinutes"/>.</summary>
    public int CrashRestartLimit { get; set; } = 3;

    public int CrashRestartWindowMinutes { get; set; } = 5;

    /// <summary>Minimizing the window hides it in the notification area instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzerothCorePanel");

    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public static PanelSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                return JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(SettingsFile)) ?? new PanelSettings();
            }
        }
        catch (Exception)
        {
            // A broken settings file must not stop the panel from opening.
        }

        return new PanelSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
    }
}
