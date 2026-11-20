using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AzerothCorePanel.Models;
using AzerothCorePanel.Services;
using AzerothCorePanel.Views;
using Microsoft.Win32;

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
        AutoRestartCheck.ToolTip =
            $"Se um servidor cair, ele é iniciado de novo em {_manager.CrashRestartDelay.TotalSeconds:0}s. " +
            $"Depois de {_manager.CrashRestartLimit} quedas em {_manager.CrashRestartWindow.TotalMinutes:0} " +
            "minutos o painel desiste, para não ficar em loop.";
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        LoadStartWithWindows();
        StartServersOnAutostartCheck.IsChecked = _settings.StartServersOnAutostart;
        ApplyPaths();

        _manager.Auth.OutputReceived += (_, line, isError) => _authLog.Append(line, isError);
        _manager.World.OutputReceived += (_, line, isError) => _worldLog.Append(line, isError);
        _manager.Auth.StateChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateUi));
        _manager.World.StateChanged += world => Dispatcher.BeginInvoke(new Action(() => OnWorldStateChanged(world)));
        _manager.Alert += (title, message, isError) =>
            Dispatcher.BeginInvoke(new Action(() => _tray.ShowNotification(title, message, isError)));

        _tray.OpenRequested += RestoreFromTray;
        _tray.StartRequested += StartServers;
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
            _tray.ShowNotification("AzerothCore CPanel", "Iniciado com o Windows. Clique no ícone para abrir.", false);
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
            _worldLog.Append("[Painel] Os servidores não foram iniciados: escolha a pasta do servidor.", true);
            _tray.ShowNotification("Servidores não iniciados", "Abra o painel e escolha a pasta do servidor.", true);
            return;
        }

        if (DatabaseWaiter.FindLoginDatabase(_settings.WorldserverPath) is (string host, int port))
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(10, _settings.MySqlWaitSeconds));
            _worldLog.Append($"[Painel] Aguardando o MySQL em {host}:{port} (até {timeout.TotalSeconds:0}s)...", false);

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
                _worldLog.Append($"[Painel] O MySQL não respondeu em {timeout.TotalSeconds:0}s; " +
                    "os servidores não foram iniciados.", true);
                _tray.ShowNotification("MySQL não respondeu",
                    "Os servidores não foram iniciados. Abra o painel e clique em Start.", true);
                return;
            }
        }

        // The panel may have been closed, or the servers started by hand, while waiting.
        if (_closed || _manager.AnyRunning)
        {
            return;
        }

        _worldLog.Append("[Painel] Iniciando os servidores (painel aberto com o Windows).", false);
        StartServers();
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

    private void ApplyPaths()
    {
        FolderBox.Text = string.IsNullOrEmpty(_settings.ServerFolder)
            ? "Nenhuma pasta selecionada"
            : _settings.ServerFolder;

        AuthPathText.Text = "Authserver: " + DescribePath(_settings.AuthserverPath);
        WorldPathText.Text = "Worldserver: " + DescribePath(_settings.WorldserverPath);

        _manager.Auth.ExePath = _settings.AuthserverPath;
        _manager.World.ExePath = _settings.WorldserverPath;
    }

    private static string DescribePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "não encontrado";
        }

        return File.Exists(path) ? path : $"{path} (arquivo não existe mais)";
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Selecione a pasta da Build ou do Repack do AzerothCore",
        };

        if (Directory.Exists(_settings.ServerFolder))
        {
            dialog.InitialDirectory = _settings.ServerFolder;
        }

        if (dialog.ShowDialog(this) != true)
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
                "Não encontrei o authserver.exe e o worldserver.exe nessa pasta.\n\n" +
                "Selecione a pasta raiz do Repack ou a pasta da Build (por exemplo, onde fica a pasta bin).",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => StartServers();

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopServersAsync();

    private async void RestartButton_Click(object sender, RoutedEventArgs e) => await RestartServersAsync();

    private void StartServers()
    {
        if (_busy || !PathsValid || _manager.AllRunning)
        {
            return;
        }

        IReadOnlyList<string> foreign = _manager.FindForeignServers();
        if (foreign.Count > 0)
        {
            MessageBoxResult answer = ShowMessage(
                "Já existe servidor rodando fora do painel:\n\n" + string.Join("\n", foreign) +
                "\n\nIniciar outro pode causar conflito de portas. Continuar mesmo assim?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            _manager.StartAll();
        }
        catch (Exception ex)
        {
            ShowError("Não foi possível iniciar os servidores", ex);
        }

        UpdateUi();
    }

    private async Task StopServersAsync()
    {
        if (!_busy)
        {
            await RunBusyAsync(_manager.StopAllAsync, "Não foi possível parar os servidores");
        }
    }

    private async Task RestartServersAsync()
    {
        if (!_busy && PathsValid)
        {
            await RunBusyAsync(_manager.RestartAllAsync, "Não foi possível reiniciar os servidores");
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
            ShowError("Não foi possível enviar o comando", ex);
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
            ShowError("Não foi possível mudar a opção de iniciar com o Windows", ex);
        }

        LoadStartWithWindows();
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
                _tray.ShowNotification("AzerothCore CPanel",
                    "O painel continua rodando aqui. Clique no ícone para abrir.", false);
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
        _ = RefreshPlayersAsync();

        // Only worth a notification when the panel is not on screen.
        if (world.State == ServerState.Running && !IsVisible)
        {
            _tray.ShowNotification("Worldserver online", "O servidor terminou de carregar.", false);
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
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
            "Os servidores ainda estão rodando.\n\n" +
            "Desligar os servidores (salvando os personagens) e fechar o painel?",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(_manager.StopAllAsync, "Não foi possível parar os servidores");
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

        UpdateStatus();

        _tray.Update(canStart, canStop, canRestart,
            $"AzerothCore CPanel\n{_manager.World.Name}: {StateLabel(_manager.World).Label}\n" +
            $"{_manager.Auth.Name}: {StateLabel(_manager.Auth).Label}" +
            (_onlinePlayers is int online ? $"\nJogadores online: {online}" : string.Empty));
    }

    private void UpdateStatus()
    {
        ShowState(_manager.Auth, AuthDot, AuthStatusText);
        ShowState(_manager.World, WorldDot, WorldStatusText);
    }

    private (Brush Color, string Label) StateLabel(ServerProcess server) => server.State switch
    {
        ServerState.Starting => (Brushes.Orange, "Iniciando..."),
        ServerState.Running => (Brushes.LimeGreen, "Online"),
        ServerState.Stopping => (Brushes.Orange, "Parando..."),
        _ when _manager.IsRestartPending(server) => (Brushes.OrangeRed, "Caiu, reiniciando..."),
        _ => (Brushes.Gray, "Parado"),
    };

    private void ShowState(ServerProcess server, Ellipse dot, TextBlock text)
    {
        (Brush color, string label) = StateLabel(server);

        if (server.State == ServerState.Running && server.StartedAt is DateTime startedAt)
        {
            TimeSpan uptime = DateTime.Now - startedAt;
            label += $" há {(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        }

        dot.Fill = color;
        text.Text = $"{server.Name}: {label}";
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
            ShowPlayers(null, "não encontrei o CharacterDatabaseInfo no worldserver.conf.");
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
        PlayersTab.Header = players is null ? "Jogadores" : $"Jogadores ({players.Count})";

        if (error is not null)
        {
            PlayersStatusText.Text = "Jogadores online: erro";
            PlayersStatusText.ToolTip = error;
            PlayersInfoText.Text = "Não foi possível consultar o banco characters: " + error;

            // Log each different error once, not every 15 seconds.
            if (error != _lastPlayersError)
            {
                _worldLog.Append("[Painel] Jogadores online: não foi possível consultar o banco: " + error, true);
            }

            _lastPlayersError = error;
            return;
        }

        _lastPlayersError = null;

        if (players is null)
        {
            PlayersStatusText.Text = "Jogadores online: —";
            PlayersStatusText.ToolTip = null;
            PlayersInfoText.Text = "O worldserver não está rodando.";
            return;
        }

        int alliance = players.Count(p => p.Faction == "Aliança");
        string updated = $"atualizado às {DateTime.Now:HH:mm:ss} (a cada 15s)";
        PlayersStatusText.Text = $"Jogadores online: {players.Count}";
        PlayersStatusText.ToolTip = $"Aliança: {alliance} · Horda: {players.Count - alliance} — {updated}";
        PlayersInfoText.Text = players.Count == 0
            ? $"Nenhum jogador online — {updated}"
            : $"{players.Count} online (Aliança: {alliance} · Horda: {players.Count - alliance}) — {updated}";
    }

    private void UpdateMetrics()
    {
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
            cpuText.Foreground = ramText.Foreground = Brushes.Gray;
            return;
        }

        cpuText.Text = m.CpuPercent is double cpu ? $"CPU {cpu:0.0}%" : "CPU …";
        ramText.Text = $"RAM {FormatBytes(m.WorkingSetBytes)}";
        ramText.ToolTip = $"Memória física em uso (working set). Pico desde o início: {FormatBytes(m.PeakWorkingSetBytes)}";
        cpuText.Foreground = ramText.Foreground = SystemColors.ControlTextBrush;
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
            ShowError("Não foi possível salvar as configurações", ex);
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
