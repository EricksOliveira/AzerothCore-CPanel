using System.Windows;
using AzerothCorePanel.Localization;
using AzerothCorePanel.Models;
using AzerothCorePanel.Services;
using AzerothCorePanel.Views;

namespace AzerothCorePanel;

public partial class App : Application
{
    // Per user session: two panels controlling the same servers would fight each other.
    private const string MutexName = @"Local\AzerothCorePanel.SingleInstance";
    private const string ShowEventName = @"Local\AzerothCorePanel.Show";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Already running (maybe hidden in the tray): ask that instance to show its window, then exit.
            try
            {
                using EventWaitHandle show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        bool launchedAtStartup = e.Args.Contains(WindowsStartup.AutostartArgument, StringComparer.OrdinalIgnoreCase);

        // Theme, flags and language must be in place before the window is created, so it is drawn right away.
        PanelSettings settings = PanelSettings.Load();
        ThemeManager.Initialize(this,
            Enum.TryParse(settings.Theme, ignoreCase: true, out AppTheme theme) ? theme : AppTheme.System);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Localization/Flags.xaml"),
        });
        L.Set(L.FromCode(settings.Language));

        var window = new MainWindow();
        MainWindow = window;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(window.RestoreFromTray)), null, Timeout.Infinite, false);

        window.ShowForLaunch(launchedAtStartup);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();

        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
