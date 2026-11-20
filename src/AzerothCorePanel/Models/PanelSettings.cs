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
