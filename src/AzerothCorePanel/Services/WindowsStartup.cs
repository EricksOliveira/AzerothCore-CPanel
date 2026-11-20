using AzerothCorePanel.Localization;
using Microsoft.Win32;

namespace AzerothCorePanel.Services;

/// <summary>
/// Turns "start with Windows" on and off through the current user's Run registry key
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). No administrator rights are needed.
/// </summary>
public static class WindowsStartup
{
    /// <summary>Command-line argument that marks a launch made by Windows at sign-in.</summary>
    public const string AutostartArgument = "--autostart";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AzerothCorePanel";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void Enable()
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException(L.T("Startup.NoPath"));

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{exePath}\" {AutostartArgument}");
    }

    public static void Disable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Points the Run entry at this exe again, in case the panel was moved to another folder.</summary>
    public static void RefreshPath()
    {
        if (IsEnabled())
        {
            Enable();
        }
    }
}
