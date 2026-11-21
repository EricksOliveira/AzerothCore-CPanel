using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace AzerothCorePanel.Views;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Switches between the light and dark color dictionaries at runtime and keeps "System" in sync with the
/// Windows app theme setting.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightColors = new("pack://application:,,,/AzerothCorePanel;component/Themes/Light.xaml");
    private static readonly Uri DarkColors = new("pack://application:,,,/AzerothCorePanel;component/Themes/Dark.xaml");
    private static readonly Uri Controls = new("pack://application:,,,/AzerothCorePanel;component/Themes/Controls.xaml");

    // DWMWA_USE_IMMERSIVE_DARK_MODE: dark title bar on Windows 10 2004+ and Windows 11.
    private const int DwmUseImmersiveDarkMode = 20;

    private static ResourceDictionary? _colors;

    public static AppTheme Selected { get; private set; } = AppTheme.System;

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the colors changed, on the UI thread.</summary>
    public static event Action? Changed;

    public static void Initialize(Application app, AppTheme theme)
    {
        _colors = new ResourceDictionary { Source = LightColors };
        app.Resources.MergedDictionaries.Add(_colors);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Controls });

        Apply(theme);

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Selected == AppTheme.System && e.Category == UserPreferenceCategory.General)
            {
                app.Dispatcher.BeginInvoke(new Action(() => Apply(AppTheme.System)));
            }
        };
    }

    public static void Apply(AppTheme theme)
    {
        Selected = theme;
        bool dark = theme == AppTheme.Dark || (theme == AppTheme.System && WindowsUsesDarkTheme());

        if (_colors is not null && (dark != IsDark || _colors.Source is null))
        {
            _colors.Source = dark ? DarkColors : LightColors;
        }

        IsDark = dark;

        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window);
        }

        Changed?.Invoke();
    }

    /// <summary>Colors the window's title bar; call again once the window has a handle (SourceInitialized).</summary>
    public static void ApplyTitleBar(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int value = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
