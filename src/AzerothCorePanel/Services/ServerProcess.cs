using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AzerothCorePanel.Services;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>
/// One AzerothCore server process (authserver or worldserver) started with redirected console streams.
/// </summary>
public sealed class ServerProcess
{
    // AzerothCore writes console output converted to the OEM code page (CharToOemBuffW in vutf8printf),
    // and reads console commands as UTF-8.
    private static readonly Encoding OutputEncoding;
    private static readonly Encoding InputEncoding = new UTF8Encoding(false);

    static ServerProcess()
    {
        // OEM code pages (850, 437, ...) are not built into .NET without this provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        OutputEncoding = Encoding.GetEncoding((int)GetOEMCP());
    }

    private readonly JobObject _job;
    private readonly object _lock = new();
    private Process? _process;
    private TaskCompletionSource<int>? _exitTcs;
    private bool _stopRequested;

    internal ServerProcess(string name, JobObject job, string? shutdownCommand, string? readyMarker)
    {
        Name = name;
        _job = job;
        ShutdownCommand = shutdownCommand;
        ReadyMarker = readyMarker;
    }

    public string Name { get; }

    public string ExePath { get; set; } = string.Empty;

    /// <summary>
    /// Console command that shuts the server down cleanly. Null means the process is simply ended,
    /// which is safe for authserver because it keeps no game state.
    /// </summary>
    public string? ShutdownCommand { get; }

    /// <summary>
    /// Text in the console output that means the server finished loading. Null means it is ready at once.
    /// </summary>
    public string? ReadyMarker { get; }

    public ServerState State { get; private set; } = ServerState.Stopped;

    public DateTime? StartedAt { get; private set; }

    public bool IsRunning
    {
        get
        {
            Process? process = _process;
            try
            {
                return process is not null && !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public event Action<ServerProcess>? StateChanged;

    /// <summary>Raised for each console line: (server, line, isError).</summary>
    public event Action<ServerProcess, string, bool>? OutputReceived;

    /// <summary>Raised when the process ends: (server, exitCode, stopWasRequested).</summary>
    public event Action<ServerProcess, int, bool>? Exited;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        if (!File.Exists(ExePath))
        {
            throw new FileNotFoundException($"{Name} não encontrado.", ExePath);
        }

        var startInfo = new ProcessStartInfo(ExePath)
        {
            // AzerothCore loads "configs/*.conf" relative to the working directory.
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = InputEncoding,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding,
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutput(e.Data, false);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data, true);
        process.Exited += OnProcessExited;

        lock (_lock)
        {
            _stopRequested = false;
            _exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _process = process;
        }

        SetState(ServerState.Starting);

        try
        {
            process.Start();
        }
        catch
        {
            lock (_lock)
            {
                _process = null;
            }

            process.Dispose();
            SetState(ServerState.Stopped);
            throw;
        }

        try
        {
            _job.Assign(process);
        }
        catch (Exception ex)
        {
            Report($"Aviso: não foi possível vincular o processo ao painel ({ex.Message}).", true);
        }

        StartedAt = DateTime.Now;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Report($"{Name} iniciado (PID {process.Id}).", false);

        if (ReadyMarker is null)
        {
            SetState(ServerState.Running);
        }
    }

    public void SendCommand(string command)
    {
        Process? process = _process;
        if (process is null || !IsRunning)
        {
            throw new InvalidOperationException($"{Name} não está rodando.");
        }

        process.StandardInput.WriteLine(command);
        process.StandardInput.Flush();
    }

    /// <summary>
    /// Sends the shutdown command and waits for the process to exit; ends it by force after the timeout.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        Process? process;
        Task<int> exitTask;

        lock (_lock)
        {
            process = _process;
            if (process is null || _exitTcs is null || !IsRunning)
            {
                return;
            }

            _stopRequested = true;
            exitTask = _exitTcs.Task;
        }

        SetState(ServerState.Stopping);

        if (ShutdownCommand is not null)
        {
            Report($"Enviando '{ShutdownCommand}'...", false);
            try
            {
                SendCommand(ShutdownCommand);
            }
            catch (Exception ex)
            {
                Report($"Não foi possível enviar o comando de desligamento: {ex.Message}", true);
            }

            if (await Task.WhenAny(exitTask, Task.Delay(timeout)) == exitTask)
            {
                return;
            }

            Report($"{Name} não fechou em {timeout.TotalSeconds:0}s; forçando encerramento.", true);
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process already exited.
        }

        await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private void OnOutput(string? line, bool isError)
    {
        if (line is null)
        {
            return;
        }

        OutputReceived?.Invoke(this, line, isError);

        if (State == ServerState.Starting && ReadyMarker is not null
            && line.Contains(ReadyMarker, StringComparison.OrdinalIgnoreCase))
        {
            SetState(ServerState.Running);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = (Process)sender!;
        int exitCode;

        try
        {
            // Without a timeout this also waits until the redirected output is fully read.
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Exception)
        {
            exitCode = -1;
        }

        bool stopRequested;
        TaskCompletionSource<int>? exitTcs;

        lock (_lock)
        {
            if (!ReferenceEquals(_process, process))
            {
                process.Dispose();
                return;
            }

            stopRequested = _stopRequested;
            exitTcs = _exitTcs;
            _process = null;
        }

        process.Dispose();
        StartedAt = null;
        Report($"{Name} encerrado (código de saída {exitCode}).", !stopRequested && exitCode != 0);
        SetState(ServerState.Stopped);
        exitTcs?.TrySetResult(exitCode);
        Exited?.Invoke(this, exitCode, stopRequested);
    }

    /// <summary>Writes a panel message into this server's log.</summary>
    internal void Report(string message, bool isError) =>
        OutputReceived?.Invoke(this, $"[Painel] {message}", isError);

    private void SetState(ServerState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
