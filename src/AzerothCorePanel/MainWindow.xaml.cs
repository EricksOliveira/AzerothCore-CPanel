using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AzerothCorePanel.Models;
using AzerothCorePanel.Localization;
using AzerothCorePanel.Services;
using AzerothCorePanel.Services.MySql;
using AzerothCorePanel.Views;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace AzerothCorePanel;

public partial class MainWindow : Window
{
    private readonly PanelSettings _settings;
    private readonly ServerManager _manager = new();
    private readonly TrayIcon _tray = new();
    private readonly LogPane _authLog;
    private readonly LogPane _worldLog;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _metricsTimer;
    private readonly DispatcherTimer _playersTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private IMySqlController? _mySql;
    private MySqlState _mySqlState = MySqlState.Unknown;
    private readonly DispatcherTimer _backupTimer;
    private bool _backupRunning;
    private bool _loadingBackupOptions;
    private DateTime _lastBackupFailureUtc = DateTime.MinValue;
    private readonly List<string> _gmHistory = new();
    private int _gmHistoryIndex;
    private bool _gmRunning;
    private bool _playersRefreshing;
    private int? _onlinePlayers;
    private string? _lastPlayersError;
    private bool _busy;
    private bool _closeConfirmed;
    private bool _closed;
    private bool _trayHintShown;
    private bool _updatingStartupCheck;

    public MainWindow()
    {
        InitializeComponent();

        _authLog = new LogPane(AuthLogBox);
        _worldLog = new LogPane(WorldLogBox);

        _settings = PanelSettings.Load();
        _manager.ShutdownTimeout = TimeSpan.FromSeconds(Math.Max(5, _settings.ShutdownTimeoutSeconds));
        _manager.AutoRestartOnCrash = _settings.AutoRestartOnCrash;
        _manager.CrashRestartDelay = TimeSpan.FromSeconds(Math.Max(1, _settings.CrashRestartDelaySeconds));
        _manager.CrashRestartLimit = Math.Max(1, _settings.CrashRestartLimit);
        _manager.CrashRestartWindow = TimeSpan.FromMinutes(Math.Max(1, _settings.CrashRestartWindowMinutes));
        AutoRestartCheck.IsChecked = _settings.AutoRestartOnCrash;
        AutoRestartCheck.ToolTip = AutoRestartTip();
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        LoadStartWithWindows();
        StartServersOnAutostartCheck.IsChecked = _settings.StartServersOnAutostart;
        StartMySqlWithServersCheck.IsChecked = _settings.StartMySqlWithServers;
        ToolTipService.SetShowOnDisabled(MySqlStopButton, true);
        ThemeCombo.SelectedItem = ThemeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == ThemeManager.Selected.ToString()) ?? ThemeCombo.Items[0];
        ThemeManager.Changed += OnThemeChanged;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _tray.SetDark(ThemeManager.IsDark);
        UpdateLanguageButton();
        L.Changed += OnLanguageChanged;

        ConfigEditorView.Ask = ShowMessage;
        ConfigEditorView.Saved += OnConfigSaved;
        SoapUserBox.Text = _settings.SoapUser;
        SoapPasswordBox.Password = SecretProtector.Unprotect(_settings.SoapPasswordProtected);
        ApplyPaths();
        ShowPlayers(null, null);

        _manager.Auth.OutputReceived += (_, line, isError) => _authLog.Append(line, isError);
        _manager.World.OutputReceived += (_, line, isError) => _worldLog.Append(line, isError);
        _manager.Auth.StateChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateUi));
        _manager.World.StateChanged += world => Dispatcher.BeginInvoke(new Action(() => OnWorldStateChanged(world)));
        _manager.Alert += (title, message, isError) =>
            Dispatcher.BeginInvoke(new Action(() => _tray.ShowNotification(title, message, isError)));

        _tray.OpenRequested += RestoreFromTray;
        _tray.StartRequested += async () => await StartServersAsync();
        _tray.StopRequested += async () => await StopServersAsync();
        _tray.RestartRequested += async () => await RestartServersAsync();
        _tray.ExitRequested += () =>
        {
            // Closing shows the "servers still running" question, so bring the window back first.
            RestoreFromTray();
            Close();
        };

        // Flushes logs and refreshes the uptime a few times per second.
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) =>
        {
            _authLog.Flush();
            _worldLog.Flush();
            UpdateUi();
        };
        _uiTimer.Start();

        // CPU is measured between samples, so a steady one-second interval gives stable numbers.
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _metricsTimer.Start();

        _playersTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _playersTimer.Tick += async (_, _) => await RefreshPlayersAsync();
        _playersTimer.Start();

        LoadBackupOptions();
        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _backupTimer.Tick += async (_, _) => await CheckScheduledBackupAsync();
        _backupTimer.Start();

        UpdateUi();
    }

    private bool PathsValid => File.Exists(_settings.AuthserverPath) && File.Exists(_settings.WorldserverPath);

    /// <summary>Shows the window, or keeps it in the tray when Windows opened the panel at sign-in.</summary>
    internal void ShowForLaunch(bool launchedAtStartup)
    {
        if (!launchedAtStartup)
        {
            Show();
            return;
        }

        if (_settings.MinimizeToTray)
        {
            // The window is first shown when the tray icon is clicked.
            _trayHintShown = true;
            _tray.ShowNotification("AzerothCore CPanel", L.T("Tray.StartedWithWindows"), false);
        }
        else
        {
            WindowState = WindowState.Minimized;
            Show();
        }

        if (_settings.StartServersOnAutostart)
        {
            _ = StartServersAtSignInAsync();
        }
    }

    private async Task StartServersAtSignInAsync()
    {
        if (!PathsValid)
        {
            _worldLog.Append(L.Log(L.T("Log.ServersNotStartedNoFolder")), true);
            _tray.ShowNotification(L.T("Tray.ServersNotStarted"), L.T("Tray.ChooseFolder"), true);
            return;
        }

        // A service set to start automatically is usually already starting; only start MySQL when it is stopped.
        if (_settings.StartMySqlWithServers && _mySql?.GetState() == MySqlState.Stopped
            && !await StartMySqlAsync())
        {
            _tray.ShowNotification(L.T("Tray.MySqlNotStarted"), L.T("Tray.SeeLog"), true);
            return;
        }

        if (DatabaseWaiter.FindLoginDatabase(_settings.WorldserverPath) is (string host, int port))
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(10, _settings.MySqlWaitSeconds));
            _worldLog.Append(L.Log(L.F("Log.WaitingMySql", host, port, timeout.TotalSeconds.ToString("0"))), false);

            bool ready;
            try
            {
                ready = await DatabaseWaiter.WaitAsync(host, port, timeout, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!ready)
            {
                _worldLog.Append(L.Log(L.F("Log.MySqlNoAnswer", timeout.TotalSeconds.ToString("0"))), true);
                _tray.ShowNotification(L.T("Tray.MySqlNoAnswer"), L.T("Tray.ClickStart"), true);
                return;
            }
        }

        // The panel may have been closed, or the servers started by hand, while waiting.
        if (_closed || _manager.AnyRunning)
        {
            return;
        }

        _worldLog.Append(L.Log(L.T("Log.StartingAtSignIn")), false);
        await StartServersAsync();
    }

    private void LoadStartWithWindows()
    {
        _updatingStartupCheck = true;
        try
        {
            // The registry is the source of truth; refresh it in case the panel exe was moved.
            WindowsStartup.RefreshPath();
            StartWithWindowsCheck.IsChecked = WindowsStartup.IsEnabled();
        }
        catch (Exception)
        {
            StartWithWindowsCheck.IsChecked = false;
        }
        finally
        {
            _updatingStartupCheck = false;
        }

        StartServersOnAutostartCheck.IsEnabled = StartWithWindowsCheck.IsChecked == true;
    }

    private void UpdatePathTexts()
    {
        FolderBox.Text = string.IsNullOrEmpty(_settings.ServerFolder)
            ? L.T("Main.NoFolder")
            : _settings.ServerFolder;

        AuthPathText.Text = "Authserver: " + DescribePath(_settings.AuthserverPath);
        WorldPathText.Text = "Worldserver: " + DescribePath(_settings.WorldserverPath);
    }

    private string AutoRestartTip() => L.F("Main.AutoRestart.Tip",
        _manager.CrashRestartDelay.TotalSeconds.ToString("0"), _manager.CrashRestartLimit,
        _manager.CrashRestartWindow.TotalMinutes.ToString("0"));

    private void ApplyPaths()
    {
        UpdatePathTexts();

        _manager.Auth.ExePath = _settings.AuthserverPath;
        _manager.World.ExePath = _settings.WorldserverPath;
        ConfigEditorView.SetServerPaths(_settings.AuthserverPath, _settings.WorldserverPath, reload: true);

        // The LoginDatabaseInfo account is read when needed, so edits to worldserver.conf are picked up.
        _mySql = MySqlDetector.Detect(_settings.ServerFolder,
            () => DatabaseInfo.Read(_settings.WorldserverPath, "LoginDatabaseInfo"));
        _mySqlState = _mySql?.GetState() ?? MySqlState.Unknown;

        UpdateSoapStatus();

        // The backup UI exists only after the constructor loaded it (ApplyPaths also runs before that).
        if (_backupTimer is not null)
        {
            UpdateBackupView();
        }
    }

    private static string DescribePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return L.T("Main.NotFound");
        }

        return File.Exists(path) ? path : L.F("Main.FileGone", path);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L.T("Dialog.ChooseServerFolder"),
        };

        if (Directory.Exists(_settings.ServerFolder))
        {
            dialog.InitialDirectory = _settings.ServerFolder;
        }

        if (dialog.ShowDialog(this) != true || !ConfigEditorView.ConfirmDiscardOrSave())
        {
            return;
        }

        (string? auth, string? world) = ServerPathDetector.Detect(dialog.FolderName);

        _settings.ServerFolder = dialog.FolderName;
        _settings.AuthserverPath = auth ?? string.Empty;
        _settings.WorldserverPath = world ?? string.Empty;
        SaveSettings();
        ApplyPaths();
        UpdateUi();

        if (auth is null || world is null)
        {
            ShowMessage(
                L.T("Msg.ExesNotFound"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartServersAsync();

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopServersAsync();

    private async void RestartButton_Click(object sender, RoutedEventArgs e) => await RestartServersAsync();

    private async void MySqlStartButton_Click(object sender, RoutedEventArgs e) => await StartMySqlAsync();

    private async void MySqlStopButton_Click(object sender, RoutedEventArgs e) => await StopMySqlAsync();

    /// <summary>Starts MySQL and waits until it accepts connections. Returns false (after telling the user) on failure.</summary>
    private async Task<bool> StartMySqlAsync()
    {
        IMySqlController? mySql = _mySql;
        if (_busy || mySql is null)
        {
            return false;
        }

        bool started = false;
        await RunBusyAsync(async () =>
        {
            _worldLog.Append(L.Log(L.F("Log.StartingMySql", mySql.Description)), false);
            await mySql.StartAsync(_lifetime.Token);

            // A running service is not necessarily accepting connections yet.
            if (DatabaseWaiter.FindLoginDatabase(_settings.WorldserverPath) is (string host, int port)
                && !await DatabaseWaiter.WaitAsync(host, port, TimeSpan.FromSeconds(60), _lifetime.Token))
            {
                throw new TimeoutException(L.F("Err.MySqlNoConnections", host, port));
            }

            _worldLog.Append(L.Log(L.T("Log.MySqlReady")), false);
            started = true;
        }, L.T("Err.StartMySql"));

        if (!started)
        {
            _worldLog.Append(L.Log(L.T("Log.MySqlNotStarted")), true);
        }

        return started;
    }

    private async Task StopMySqlAsync()
    {
        IMySqlController? mySql = _mySql;
        if (_busy || mySql is null)
        {
            return;
        }

        if (_manager.AnyRunning || _manager.AnyRestartPending)
        {
            ShowMessage(L.T("Msg.StopServersFirst"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync(async () =>
        {
            _worldLog.Append(L.Log(L.F("Log.StoppingMySql", mySql.Description)), false);
            await mySql.StopAsync(_lifetime.Token);
            _worldLog.Append(L.Log(L.T("Log.MySqlStopped")), false);
        }, L.T("Err.StopMySql"));
    }

    private async Task StartServersAsync()
    {
        if (_busy || !PathsValid || _manager.AllRunning)
        {
            return;
        }

        IReadOnlyList<string> foreign = _manager.FindForeignServers();
        if (foreign.Count > 0)
        {
            MessageBoxResult answer = ShowMessage(
                L.F("Msg.ForeignServers", string.Join("\n", foreign)),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (_settings.StartMySqlWithServers && _mySql?.GetState() == MySqlState.Stopped
            && !await StartMySqlAsync())
        {
            return;
        }

        try
        {
            _manager.StartAll();
        }
        catch (Exception ex)
        {
            ShowError(L.T("Err.StartServers"), ex);
        }

        UpdateUi();
    }

    private async Task StopServersAsync()
    {
        if (!_busy)
        {
            await RunBusyAsync(_manager.StopAllAsync, L.T("Err.StopServers"));
        }
    }

    private async Task RestartServersAsync()
    {
        if (!_busy && PathsValid)
        {
            await RunBusyAsync(_manager.RestartAllAsync, L.T("Err.RestartServers"));
        }
    }

    private void SendCommandButton_Click(object sender, RoutedEventArgs e) => SendCommand();

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendCommand();
            e.Handled = true;
        }
    }

    private void SendCommand()
    {
        string command = CommandBox.Text.Trim();
        if (command.Length == 0 || !_manager.World.IsRunning)
        {
            return;
        }

        try
        {
            _worldLog.Append($"> {command}", false);
            _manager.World.SendCommand(command);
            CommandBox.Clear();
        }
        catch (Exception ex)
        {
            ShowError(L.T("Err.SendCommand"), ex);
        }
    }

    private void AutoScrollCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = AutoScrollCheck.IsChecked == true;

        // The handler can run during InitializeComponent, before the panes exist.
        if (_authLog is not null && _worldLog is not null)
        {
            _authLog.AutoScroll = enabled;
            _worldLog.AutoScroll = enabled;
        }
    }

    private void AutoRestartCheck_Changed(object sender, RoutedEventArgs e)
    {
        // The handler can run during InitializeComponent, before the settings are loaded.
        if (_settings is null)
        {
            return;
        }

        bool enabled = AutoRestartCheck.IsChecked == true;
        _manager.AutoRestartOnCrash = enabled;

        if (_settings.AutoRestartOnCrash != enabled)
        {
            _settings.AutoRestartOnCrash = enabled;
            SaveSettings();
        }
    }

    private void MinimizeToTrayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        bool enabled = MinimizeToTrayCheck.IsChecked == true;
        if (_settings.MinimizeToTray != enabled)
        {
            _settings.MinimizeToTray = enabled;
            SaveSettings();
        }
    }

    private void StartWithWindowsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _updatingStartupCheck)
        {
            return;
        }

        bool enabled = StartWithWindowsCheck.IsChecked == true;
        try
        {
            if (enabled)
            {
                WindowsStartup.Enable();
            }
            else
            {
                WindowsStartup.Disable();
            }
        }
        catch (Exception ex)
        {
            ShowError(L.T("Err.StartWithWindows"), ex);
        }

        LoadStartWithWindows();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings is null || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out AppTheme theme) || theme == ThemeManager.Selected)
        {
            return;
        }

        ThemeManager.Apply(theme);
        _settings.Theme = tag;
        SaveSettings();
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        LanguageMenu.PlacementTarget = LanguageButton;
        LanguageMenu.IsOpen = true;
    }

    private void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string code })
        {
            return;
        }

        Localization.Language language = L.FromCode(code);
        if (language == L.Current)
        {
            return;
        }

        L.Set(language);
        _settings.Language = L.Code(language);
        SaveSettings();
    }

    /// <summary>Flag and code of the current language on the footer button; the current one is bold in the menu.</summary>
    private void UpdateLanguageButton()
    {
        string code = L.Code(L.Current);
        LanguageFlag.Source = (ImageSource)FindResource($"Flag.{code}");
        LanguageCode.Text = code.ToUpperInvariant();

        foreach (MenuItem item in LanguageMenu.Items.OfType<MenuItem>())
        {
            item.FontWeight = (string)item.Tag == code ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    /// <summary>XAML texts update by themselves; texts written from code are rebuilt here.</summary>
    private void OnLanguageChanged()
    {
        UpdateLanguageButton();

        // A ComboBox keeps showing the old text of its selected item until the selection changes.
        _loadingBackupOptions = true;
        foreach (ComboBox combo in new[] { ThemeCombo, BackupIntervalCombo, BackupKeepCombo })
        {
            object? selected = combo.SelectedItem;
            combo.SelectedItem = null;
            combo.SelectedItem = selected;
        }

        _loadingBackupOptions = false;

        AutoRestartCheck.ToolTip = AutoRestartTip();
        UpdatePathTexts();
        UpdateUi();
        UpdateMetrics();
        UpdateSoapStatus();
        UpdateBackupView();
        PlayersGrid.Items.Refresh();
        _ = RefreshPlayersAsync();
    }

    private void OnThemeChanged()
    {
        _tray.SetDark(ThemeManager.IsDark);

        // Texts colored from code pick up the new colors.
        UpdateMetrics();
    }

    private void StartMySqlWithServersCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        bool enabled = StartMySqlWithServersCheck.IsChecked == true;
        if (_settings.StartMySqlWithServers != enabled)
        {
            _settings.StartMySqlWithServers = enabled;
            SaveSettings();
        }
    }

    private void StartServersOnAutostartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        bool enabled = StartServersOnAutostartCheck.IsChecked == true;
        if (_settings.StartServersOnAutostart != enabled)
        {
            _settings.StartServersOnAutostart = enabled;
            SaveSettings();
        }
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _authLog.Clear();
        _worldLog.Clear();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
        {
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowNotification("AzerothCore CPanel", L.T("Tray.StillRunning"), false);
            }
        }
    }

    internal void RestoreFromTray()
    {
        if (_closed)
        {
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // Wait for layout, so the TextBoxes know their size before scrolling.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _worldLog.ScrollToEndIfAuto();
            _authLog.ScrollToEndIfAuto();
        }));
    }

    private void OnWorldStateChanged(ServerProcess world)
    {
        UpdateUi();
        UpdateSoapStatus();
        _ = RefreshPlayersAsync();

        // Only worth a notification when the panel is not on screen.
        if (world.State == ServerState.Running && !IsVisible)
        {
            _tray.ShowNotification(L.T("Tray.WorldOnline"), L.T("Tray.WorldLoaded"), false);
        }
    }

    private void OnConfigSaved(string path)
    {
        _worldLog.Append(L.Log(L.F("Log.ConfigSaved", path, Path.GetFileName(path))), false);

        // SOAP, database and log settings come from these files.
        UpdateSoapStatus();
        UpdateBackupView();

        string name = Path.GetFileNameWithoutExtension(path);
        bool running = name.Equals("worldserver", StringComparison.OrdinalIgnoreCase)
            ? _manager.World.IsRunning
            : _manager.Auth.IsRunning;

        if (running)
        {
            ShowMessage(L.F("Msg.ConfigSavedRestart", name),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        // Unsaved .conf edits: ask once, before anything else (the answer may cancel closing).
        if (!_closeConfirmed && ConfigEditorView.HasChanges)
        {
            RestoreFromTray();
            if (!ConfigEditorView.ConfirmDiscardOrSave())
            {
                e.Cancel = true;
                return;
            }
        }

        if (_closeConfirmed || !_manager.AnyRunning)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        if (_busy)
        {
            return;
        }

        MessageBoxResult answer = ShowMessage(
            L.T("Msg.CloseWhileRunning"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(_manager.StopAllAsync, L.T("Err.StopServers"));
        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _uiTimer.Stop();
        _metricsTimer.Stop();
        _playersTimer.Stop();
        _backupTimer.Stop();
        _tray.Dispose();
        _manager.Dispose();
        base.OnClosed(e);
    }

    private async Task RunBusyAsync(Func<Task> action, string errorTitle)
    {
        _busy = true;
        UpdateUi();

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError(errorTitle, ex);
        }
        finally
        {
            _busy = false;
            UpdateUi();
        }
    }

    private void UpdateUi()
    {
        bool anyRunning = _manager.AnyRunning;
        bool active = anyRunning || _manager.AnyRestartPending;
        bool canStart = !_busy && PathsValid && !_manager.AllRunning;
        bool canStop = !_busy && active;
        bool canRestart = !_busy && PathsValid && active;

        StartButton.IsEnabled = canStart;
        StopButton.IsEnabled = canStop;
        RestartButton.IsEnabled = canRestart;
        BrowseButton.IsEnabled = !_busy && !active;

        bool canSend = _manager.World.State == ServerState.Running;
        CommandBox.IsEnabled = canSend;
        SendCommandButton.IsEnabled = canSend;

        MySqlStartButton.IsEnabled = !_busy && _mySql is not null && _mySqlState == MySqlState.Stopped;
        MySqlStopButton.IsEnabled = !_busy && _mySql is not null && _mySqlState == MySqlState.Running && !active;
        MySqlStopButton.ToolTip = active ? L.T("MySql.StopFirst.Tip") : L.T("MySql.Stop.Tip");

        UpdateStatus();

        _tray.Update(canStart, canStop, canRestart,
            $"AzerothCore CPanel\n{_manager.World.Name}: {StateLabel(_manager.World).Label}\n" +
            $"{_manager.Auth.Name}: {StateLabel(_manager.Auth).Label}" +
            (_mySql is not null ? $"\nMySQL: {MySqlLabel(_mySqlState).Label}" : string.Empty) +
            (_onlinePlayers is int online ? "\n" + L.F("Players.Online", online) : string.Empty));
    }

    private void UpdateStatus()
    {
        ShowState(_manager.Auth, AuthDot, AuthStatusText);
        ShowState(_manager.World, WorldDot, WorldStatusText);
        ShowMySqlState();
    }

    private static (Brush Color, string Label) MySqlLabel(MySqlState state) => state switch
    {
        MySqlState.Running => (Brushes.LimeGreen, L.T("State.Running")),
        MySqlState.Starting => (Brushes.Orange, L.T("State.Starting")),
        MySqlState.Stopping => (Brushes.Orange, L.T("State.Stopping")),
        MySqlState.Stopped => (Brushes.Gray, L.T("State.Stopped")),
        _ => (Brushes.Gray, L.T("State.Unknown")),
    };

    private void ShowMySqlState()
    {
        if (_mySql is null)
        {
            MySqlDot.Fill = Brushes.Gray;
            MySqlStatusText.Text = L.T("MySql.NotFound");
            MySqlStatusText.ToolTip = L.T("MySql.NotFound.Tip");
            MySqlStartButton.Visibility = MySqlStopButton.Visibility = Visibility.Collapsed;
            return;
        }

        (Brush color, string label) = MySqlLabel(_mySqlState);
        MySqlDot.Fill = color;
        MySqlStatusText.Text = $"MySQL: {label} ({_mySql.Description})";
        MySqlStatusText.ToolTip = _mySql.Description;
        MySqlStartButton.Visibility = MySqlStopButton.Visibility = Visibility.Visible;
    }

    private (Brush Color, string Label) StateLabel(ServerProcess server) => server.State switch
    {
        ServerState.Starting => (Brushes.Orange, L.T("State.Starting")),
        ServerState.Running => (Brushes.LimeGreen, L.T("State.Online")),
        ServerState.Stopping => (Brushes.Orange, L.T("State.Stopping")),
        _ when _manager.IsRestartPending(server) => (Brushes.OrangeRed, L.T("State.CrashedRestarting")),
        _ => (Brushes.Gray, L.T("State.Stopped")),
    };

    private void ShowState(ServerProcess server, Ellipse dot, TextBlock text)
    {
        (Brush color, string label) = StateLabel(server);

        if (server.State == ServerState.Running && server.StartedAt is DateTime startedAt)
        {
            TimeSpan uptime = DateTime.Now - startedAt;
            label += L.F("State.Since", $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}");
        }

        dot.Fill = color;
        text.Text = $"{server.Name}: {label}";
    }

    private void UpdateSoapStatus()
    {
        SoapConfig? soap = PathsValid ? SoapClient.ReadConfig(_settings.WorldserverPath) : null;

        if (soap is null)
        {
            SoapStatusText.Text = L.T("Soap.ChooseFolder");
            EnableSoapButton.Visibility = Visibility.Collapsed;
        }
        else if (!soap.Enabled)
        {
            SoapStatusText.Text = L.T("Soap.Disabled");
            EnableSoapButton.Visibility = Visibility.Visible;
        }
        else
        {
            SoapStatusText.Text = _manager.World.State == ServerState.Running
                ? L.F("Soap.Active", soap.Endpoint)
                : L.F("Soap.Configured", soap.Endpoint);
            EnableSoapButton.Visibility = Visibility.Collapsed;
        }
    }

    private void EnableSoapButton_Click(object sender, RoutedEventArgs e)
    {
        SoapConfig? soap = PathsValid ? SoapClient.ReadConfig(_settings.WorldserverPath) : null;
        if (soap is null)
        {
            return;
        }

        MessageBoxResult answer = ShowMessage(
            L.F("Msg.EnableSoap", soap.ConfPath, soap.Endpoint),
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            if (!SoapClient.EnableInConf(soap.ConfPath))
            {
                ShowMessage(L.T("Msg.SoapLineNotFound"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (Exception ex)
        {
            ShowError(L.T("Err.ChangeConf"), ex);
            return;
        }

        AppendGmOutput(L.Log(L.T("Gm.SoapEnabledLog")), false);
        UpdateSoapStatus();

        if (_manager.World.IsRunning)
        {
            ShowMessage(L.T("Msg.SoapEnabledRestart"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SaveSoapButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSoapCredentials();
        AppendGmOutput(L.Log(L.T("Gm.AccountSaved")), false);
    }

    private void SaveSoapCredentials()
    {
        _settings.SoapUser = SoapUserBox.Text.Trim();
        _settings.SoapPasswordProtected = SecretProtector.Protect(SoapPasswordBox.Password);
        SaveSettings();
    }

    private async void GmQuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string action })
        {
            return;
        }

        if (action.StartsWith("run:", StringComparison.Ordinal))
        {
            await RunGmCommandAsync(action[4..]);
        }
        else if (action.StartsWith("fill:", StringComparison.Ordinal))
        {
            // Templates that need more text: put them in the command box for the user to finish.
            GmTab.IsSelected = true;
            GmCommandBox.Text = action[5..];
            GmCommandBox.Focus();
            GmCommandBox.CaretIndex = GmCommandBox.Text.Length;
        }
    }

    private async void GmRunButton_Click(object sender, RoutedEventArgs e) => await RunGmCommandFromBoxAsync();

    private async void GmCommandBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await RunGmCommandFromBoxAsync();
                break;
            case Key.Up when _gmHistory.Count > 0:
                e.Handled = true;
                _gmHistoryIndex = Math.Max(0, _gmHistoryIndex - 1);
                GmCommandBox.Text = _gmHistory[_gmHistoryIndex];
                GmCommandBox.CaretIndex = GmCommandBox.Text.Length;
                break;
            case Key.Down when _gmHistory.Count > 0:
                e.Handled = true;
                _gmHistoryIndex = Math.Min(_gmHistory.Count, _gmHistoryIndex + 1);
                GmCommandBox.Text = _gmHistoryIndex < _gmHistory.Count ? _gmHistory[_gmHistoryIndex] : string.Empty;
                GmCommandBox.CaretIndex = GmCommandBox.Text.Length;
                break;
        }
    }

    private async Task RunGmCommandFromBoxAsync()
    {
        string command = GmCommandBox.Text.Trim();
        if (command.Length == 0)
        {
            return;
        }

        if (_gmHistory.Count == 0 || _gmHistory[^1] != command)
        {
            _gmHistory.Add(command);
        }

        _gmHistoryIndex = _gmHistory.Count;
        GmCommandBox.Clear();
        await RunGmCommandAsync(command);
    }

    private async void KickPlayerMenu_Click(object sender, RoutedEventArgs e)
    {
        if (PlayersGrid.SelectedItem is not OnlinePlayer player)
        {
            return;
        }

        MessageBoxResult answer = ShowMessage(L.F("Msg.KickConfirm", player.Name),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            await RunGmCommandAsync($"kick {player.Name}");
            await RefreshPlayersAsync();
        }
    }

    /// <summary>Runs a GM command through SOAP and writes it and its output to the GM tab.</summary>
    private async Task RunGmCommandAsync(string command)
    {
        // A leading dot is how commands are typed in game; the console and SOAP do not want it.
        command = command.Trim().TrimStart('.');
        if (_gmRunning || command.Length == 0)
        {
            return;
        }

        GmTab.IsSelected = true;
        AppendGmOutput($"> {command}", false);

        SoapConfig? soap = PathsValid ? SoapClient.ReadConfig(_settings.WorldserverPath) : null;
        string user = SoapUserBox.Text.Trim();
        string password = SoapPasswordBox.Password;

        string? problem = soap switch
        {
            null => L.T("Gm.ChooseFolderFirst"),
            { Enabled: false } => L.T("Gm.SoapOff"),
            _ when user.Length == 0 || password.Length == 0 => L.T("Gm.FillCredentials"),
            _ => null,
        };

        if (problem is not null)
        {
            AppendGmOutput(problem, true);
            return;
        }

        _gmRunning = true;
        GmRunButton.IsEnabled = false;
        try
        {
            SoapResult result = await SoapClient.ExecuteAsync(soap!, user, password, command, _lifetime.Token);
            AppendGmOutput(result.Output.Length > 0 ? result.Output : L.T("Gm.NoOutput"), !result.Success);

            // The account worked: keep it, so it does not have to be typed again.
            if (user != _settings.SoapUser || password != SecretProtector.Unprotect(_settings.SoapPasswordProtected))
            {
                SaveSoapCredentials();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendGmOutput(ex.Message, true);
        }
        finally
        {
            _gmRunning = false;
            GmRunButton.IsEnabled = true;
        }
    }

    private void AppendGmOutput(string text, bool isError)
    {
        string prefix = $"[{DateTime.Now:HH:mm:ss}]{(isError ? "!" : " ")} ";
        GmOutputBox.AppendText(prefix + text.Replace("\n", "\n" + new string(' ', prefix.Length)) + "\n");
        GmOutputBox.ScrollToEnd();
    }

    private string BackupFolderPath =>
        !string.IsNullOrWhiteSpace(_settings.BackupFolder) ? _settings.BackupFolder
        : Directory.Exists(_settings.ServerFolder) ? Path.Combine(_settings.ServerFolder, "Backups")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AzerothCorePanel Backups");

    private void LoadBackupOptions()
    {
        _loadingBackupOptions = true;
        BackupAuthCheck.IsChecked = _settings.BackupAuth;
        BackupCharactersCheck.IsChecked = _settings.BackupCharacters;
        BackupWorldCheck.IsChecked = _settings.BackupWorld;
        SelectByTag(BackupIntervalCombo, _settings.BackupIntervalHours);
        SelectByTag(BackupKeepCombo, _settings.BackupKeep);
        _loadingBackupOptions = false;
        UpdateBackupView();
    }

    private static void SelectByTag(ComboBox combo, int value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag && tag == value.ToString())
            ?? combo.Items[0];
    }

    private static int SelectedTag(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int value) ? value : 0;

    private void BackupOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _loadingBackupOptions)
        {
            return;
        }

        _settings.BackupAuth = BackupAuthCheck.IsChecked == true;
        _settings.BackupCharacters = BackupCharactersCheck.IsChecked == true;
        _settings.BackupWorld = BackupWorldCheck.IsChecked == true;
        _settings.BackupIntervalHours = SelectedTag(BackupIntervalCombo);
        _settings.BackupKeep = SelectedTag(BackupKeepCombo);
        SaveSettings();
        UpdateBackupView();
    }

    private void BackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("Dialog.BackupFolder") };
        if (Directory.Exists(BackupFolderPath))
        {
            dialog.InitialDirectory = BackupFolderPath;
        }

        if (dialog.ShowDialog(this) == true)
        {
            _settings.BackupFolder = dialog.FolderName;
            SaveSettings();
            UpdateBackupView();
        }
    }

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BackupFolderPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { BackupFolderPath },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            ShowError(L.T("Err.OpenFolder"), ex);
        }
    }

    private void ChooseMysqldumpButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Dialog.ChooseDump"),
            Filter = L.T("Dialog.DumpFilter"),
        };

        if (dialog.ShowDialog(this) == true)
        {
            _settings.MysqldumpPath = dialog.FileName;
            SaveSettings();
            UpdateBackupView();
        }
    }

    private async void BackupNowButton_Click(object sender, RoutedEventArgs e) => await RunBackupAsync(scheduled: false);

    private async Task CheckScheduledBackupAsync()
    {
        int hours = _settings.BackupIntervalHours;
        if (hours <= 0 || _backupRunning || _closed || !PathsValid)
        {
            return;
        }

        DateTime due = (_settings.LastBackupUtc ?? DateTime.MinValue).AddHours(hours);

        // After a failure, wait 15 minutes before trying again instead of every 30 seconds.
        if (DateTime.UtcNow < due || DateTime.UtcNow < _lastBackupFailureUtc.AddMinutes(15))
        {
            return;
        }

        // MySQL not up (for example the PC just started): try again on the next tick.
        if (DatabaseWaiter.FindLoginDatabase(_settings.WorldserverPath) is (string host, int port)
            && !await DatabaseWaiter.WaitAsync(host, port, TimeSpan.FromSeconds(2), _lifetime.Token))
        {
            return;
        }

        await RunBackupAsync(scheduled: true);
    }

    private async Task RunBackupAsync(bool scheduled)
    {
        if (_backupRunning || _closed)
        {
            return;
        }

        string? problem = null;
        var databases = new List<DatabaseInfo>();
        string? mysqldump = DatabaseBackup.FindMysqldump(_mySql, _settings.MysqldumpPath);

        if (!PathsValid)
        {
            problem = L.T("Gm.ChooseFolderFirst");
        }
        else if (mysqldump is null)
        {
            problem = L.T("Backup.DumpNotFoundProblem");
        }
        else
        {
            foreach ((bool selected, string setting) in new[]
                     {
                         (_settings.BackupAuth, "LoginDatabaseInfo"),
                         (_settings.BackupCharacters, "CharacterDatabaseInfo"),
                         (_settings.BackupWorld, "WorldDatabaseInfo"),
                     })
            {
                if (!selected)
                {
                    continue;
                }

                DatabaseInfo? info = DatabaseInfo.Read(_settings.WorldserverPath, setting);
                if (info is null)
                {
                    problem = L.F("Backup.SettingMissing", setting);
                    break;
                }

                databases.Add(info);
            }

            if (problem is null && databases.Count == 0)
            {
                problem = L.T("Backup.NoDatabase");
            }
        }

        if (problem is not null)
        {
            ReportBackupFailure(problem, scheduled);
            return;
        }

        _backupRunning = true;
        BackupNowButton.IsEnabled = false;
        string names = string.Join(", ", databases.Select(d => d.Database));
        BackupStatusText.Text = L.F("Backup.Running", names);
        _worldLog.Append(L.Log(L.F(scheduled ? "Log.BackupAuto" : "Log.BackupManual", names)), false);

        try
        {
            BackupResult result = await DatabaseBackup.RunAsync(mysqldump!, databases, BackupFolderPath,
                _settings.BackupKeep, _lifetime.Token);

            _settings.LastBackupUtc = DateTime.UtcNow;
            SaveSettings();

            string size = new BackupFile(result.ZipPath, DateTime.Now, result.Bytes).SizeText;
            _worldLog.Append(L.Log(L.F("Log.BackupSaved", result.ZipPath, size,
                result.Duration.TotalSeconds.ToString("0.0"))), false);

            if (scheduled || !IsVisible)
            {
                _tray.ShowNotification(L.T("Tray.BackupDone"), $"{Path.GetFileName(result.ZipPath)} ({size})", false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportBackupFailure(ex.Message, scheduled);
        }
        finally
        {
            _backupRunning = false;
            BackupNowButton.IsEnabled = true;
            UpdateBackupView();
        }
    }

    private void ReportBackupFailure(string message, bool scheduled)
    {
        _lastBackupFailureUtc = DateTime.UtcNow;
        _worldLog.Append(L.Log(L.F("Log.BackupFailed", message)), true);

        if (scheduled)
        {
            _tray.ShowNotification(L.T("Tray.BackupFailed"), message, true);
        }
        else
        {
            ShowMessage(L.F("Msg.BackupFailed", message), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateBackupView();
    }

    private void UpdateBackupView()
    {
        if (_settings is null || _backupRunning)
        {
            return;
        }

        BackupFolderBox.Text = BackupFolderPath;

        string? mysqldump = DatabaseBackup.FindMysqldump(_mySql, _settings.MysqldumpPath);
        MysqldumpText.Text = mysqldump is null ? L.T("Backup.DumpNotFound") : $"mysqldump: {mysqldump}";
        ChooseMysqldumpButton.Visibility = mysqldump is null || !string.IsNullOrEmpty(_settings.MysqldumpPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

        string last = _settings.LastBackupUtc is DateTime lastUtc
            ? L.F("Backup.Last", lastUtc.ToLocalTime().ToString("g", L.Culture))
            : L.T("Backup.None");

        string next;
        if (_settings.BackupIntervalHours <= 0)
        {
            next = L.T("Backup.ScheduleOff");
        }
        else
        {
            DateTime due = (_settings.LastBackupUtc ?? DateTime.UtcNow).AddHours(_settings.BackupIntervalHours);
            next = due <= DateTime.UtcNow
                ? L.T("Backup.NextSoon")
                : L.F("Backup.Next", due.ToLocalTime().ToString("g", L.Culture));
        }

        BackupStatusText.Text = $"{last} {next}";
        BackupsGrid.ItemsSource = DatabaseBackup.List(BackupFolderPath);
    }

    private async void RefreshPlayersButton_Click(object sender, RoutedEventArgs e) => await RefreshPlayersAsync();

    private async Task RefreshPlayersAsync()
    {
        if (_playersRefreshing || _closed)
        {
            return;
        }

        // characters.online is only meaningful while worldserver runs (it resets it on startup).
        if (_manager.World.State != ServerState.Running)
        {
            ShowPlayers(null, null);
            return;
        }

        string? connectionString = CharacterDatabase.BuildConnectionString(_settings.WorldserverPath);
        if (connectionString is null)
        {
            ShowPlayers(null, L.F("Backup.SettingMissing", "CharacterDatabaseInfo"));
            return;
        }

        _playersRefreshing = true;
        try
        {
            IReadOnlyList<OnlinePlayer> players =
                await CharacterDatabase.GetOnlinePlayersAsync(connectionString, _lifetime.Token);
            ShowPlayers(players, null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowPlayers(null, ex.Message);
        }
        finally
        {
            _playersRefreshing = false;
        }
    }

    private void ShowPlayers(IReadOnlyList<OnlinePlayer>? players, string? error)
    {
        if (_closed)
        {
            return;
        }

        PlayersGrid.ItemsSource = players ?? Array.Empty<OnlinePlayer>();
        _onlinePlayers = players?.Count;
        PlayersTab.Header = players is null ? L.T("Tab.Players") : L.F("Tab.PlayersCount", players.Count);

        if (error is not null)
        {
            PlayersStatusText.Text = L.T("Players.StatusError");
            PlayersStatusText.ToolTip = error;
            PlayersInfoText.Text = L.F("Players.QueryError", error);

            // Log each different error once, not every 15 seconds.
            if (error != _lastPlayersError)
            {
                _worldLog.Append(L.Log(L.F("Log.PlayersQueryError", error)), true);
            }

            _lastPlayersError = error;
            return;
        }

        _lastPlayersError = null;

        if (players is null)
        {
            PlayersStatusText.Text = L.F("Players.Online", "—");
            PlayersStatusText.ToolTip = null;
            PlayersInfoText.Text = L.T("Players.NotRunning");
            return;
        }

        int alliance = players.Count(p => p.IsAlliance);
        string updated = L.F("Players.Updated", DateTime.Now.ToString("T", L.Culture));
        PlayersStatusText.Text = L.F("Players.Online", players.Count);
        PlayersStatusText.ToolTip = L.F("Players.FactionTip", alliance, players.Count - alliance, updated);
        PlayersInfoText.Text = players.Count == 0
            ? L.F("Players.NoneOnline", updated)
            : L.F("Players.CountInfo", players.Count, alliance, players.Count - alliance, updated);
    }

    private void UpdateMetrics()
    {
        _mySqlState = _mySql?.GetState() ?? MySqlState.Unknown;

        ShowMetrics(_manager.World.SampleMetrics(), WorldCpuText, WorldRamText);
        ShowMetrics(_manager.Auth.SampleMetrics(), AuthCpuText, AuthRamText);
    }

    private static void ShowMetrics(ProcessMetrics? metrics, TextBlock cpuText, TextBlock ramText)
    {
        if (metrics is not ProcessMetrics m)
        {
            cpuText.Text = "CPU —";
            ramText.Text = "RAM —";
            ramText.ToolTip = null;
            cpuText.SetResourceReference(ForegroundProperty, "SubtleTextBrush");
            ramText.SetResourceReference(ForegroundProperty, "SubtleTextBrush");
            return;
        }

        cpuText.Text = m.CpuPercent is double cpu ? $"CPU {cpu:0.0}%" : "CPU …";
        ramText.Text = $"RAM {FormatBytes(m.WorkingSetBytes)}";
        ramText.ToolTip = L.F("Metrics.RamTip", FormatBytes(m.PeakWorkingSetBytes));
        cpuText.SetResourceReference(ForegroundProperty, "TextBrush");
        ramText.SetResourceReference(ForegroundProperty, "TextBrush");
    }

    private static string FormatBytes(long bytes)
    {
        const double MiB = 1024 * 1024;
        const double GiB = MiB * 1024;
        return bytes >= GiB ? $"{bytes / GiB:0.00} GB" : $"{bytes / MiB:0} MB";
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            ShowError(L.T("Err.SaveSettings"), ex);
        }
    }

    /// <summary>Message box owned by the window when it is on screen, standalone when it is in the tray.</summary>
    private MessageBoxResult ShowMessage(string text, MessageBoxButton buttons, MessageBoxImage image) =>
        IsVisible
            ? MessageBox.Show(this, text, Title, buttons, image)
            : MessageBox.Show(text, Title, buttons, image);

    private void ShowError(string title, Exception ex) =>
        ShowMessage($"{title}:\n\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Error);
}
