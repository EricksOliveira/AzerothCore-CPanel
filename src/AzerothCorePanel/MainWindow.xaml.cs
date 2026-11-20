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
    private readonly LogPane _authLog;
    private readonly LogPane _worldLog;
    private readonly DispatcherTimer _uiTimer;
    private bool _busy;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        _authLog = new LogPane(AuthLogBox);
        _worldLog = new LogPane(WorldLogBox);

        _settings = PanelSettings.Load();
        _manager.ShutdownTimeout = TimeSpan.FromSeconds(Math.Max(5, _settings.ShutdownTimeoutSeconds));
        ApplyPaths();

        _manager.Auth.OutputReceived += (_, line, isError) => _authLog.Append(line, isError);
        _manager.World.OutputReceived += (_, line, isError) => _worldLog.Append(line, isError);
        _manager.Auth.StateChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateUi));
        _manager.World.StateChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateUi));

        // Flushes logs and refreshes the uptime a few times per second.
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) =>
        {
            _authLog.Flush();
            _worldLog.Flush();
            UpdateStatus();
        };
        _uiTimer.Start();

        UpdateUi();
    }

    private bool PathsValid => File.Exists(_settings.AuthserverPath) && File.Exists(_settings.WorldserverPath);

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
            MessageBox.Show(this,
                "Não encontrei o authserver.exe e o worldserver.exe nessa pasta.\n\n" +
                "Selecione a pasta raiz do Repack ou a pasta da Build (por exemplo, onde fica a pasta bin).",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> foreign = _manager.FindForeignServers();
        if (foreign.Count > 0)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "Já existe servidor rodando fora do painel:\n\n" + string.Join("\n", foreign) +
                "\n\nIniciar outro pode causar conflito de portas. Continuar mesmo assim?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);

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

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(_manager.StopAllAsync, "Não foi possível parar os servidores");
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(_manager.RestartAllAsync, "Não foi possível reiniciar os servidores");
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

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _authLog.Clear();
        _worldLog.Clear();
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

        MessageBoxResult answer = MessageBox.Show(this,
            "Os servidores ainda estão rodando.\n\n" +
            "Desligar os servidores (salvando os personagens) e fechar o painel?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question);

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
        _uiTimer.Stop();
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

        StartButton.IsEnabled = !_busy && PathsValid && !_manager.AllRunning;
        StopButton.IsEnabled = !_busy && anyRunning;
        RestartButton.IsEnabled = !_busy && PathsValid && anyRunning;
        BrowseButton.IsEnabled = !_busy && !anyRunning;

        bool canSend = _manager.World.State == ServerState.Running;
        CommandBox.IsEnabled = canSend;
        SendCommandButton.IsEnabled = canSend;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        ShowState(_manager.Auth, AuthDot, AuthStatusText);
        ShowState(_manager.World, WorldDot, WorldStatusText);
    }

    private static void ShowState(ServerProcess server, Ellipse dot, TextBlock text)
    {
        (Brush color, string label) = server.State switch
        {
            ServerState.Starting => (Brushes.Orange, "Iniciando..."),
            ServerState.Running => (Brushes.LimeGreen, "Online"),
            ServerState.Stopping => (Brushes.Orange, "Parando..."),
            _ => (Brushes.Gray, "Parado"),
        };

        if (server.State == ServerState.Running && server.StartedAt is DateTime startedAt)
        {
            TimeSpan uptime = DateTime.Now - startedAt;
            label += $" há {(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        }

        dot.Fill = color;
        text.Text = $"{server.Name}: {label}";
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

    private void ShowError(string title, Exception ex) =>
        MessageBox.Show(this, $"{title}:\n\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
}
