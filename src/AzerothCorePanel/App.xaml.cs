using System.Windows;
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

        // The theme must be in place before the window is created, so it is drawn with the right colors.
        ThemeManager.Initialize(this,
            Enum.TryParse(PanelSettings.Load().Theme, ignoreCase: true, out AppTheme theme) ? theme : AppTheme.System);

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
