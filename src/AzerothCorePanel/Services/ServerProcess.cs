using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AzerothCorePanel.Localization;

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

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private readonly JobObject _job;
    private readonly object _lock = new();
    private readonly object _stdinLock = new();
    private Process? _process;
    private TaskCompletionSource<int>? _exitTcs;
    private LogFileTailer? _tailer;
    private Timer? _heartbeat;
    private bool _stopRequested;
    private volatile bool _useConsoleOutput = true;

    // Previous CPU sample, to turn total processor time into a percentage.
    private int _sampledPid;
    private TimeSpan _sampledCpuTime;
    private DateTime _sampledAtUtc;

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

    /// <summary>
    /// When set, logs are read from the log file named in this .conf instead of the console. Used for
    /// authserver: it has no console input, so its buffered console output would only show up when it exits.
    /// </summary>
    internal string? LogFileConf { get; init; }

    /// <summary>
    /// The .conf whose "Appender.Console" gets the log level prefix when logs come from the console
    /// (see <see cref="LogLevelPrefix"/>).
    /// </summary>
    internal string? ConsoleConf { get; init; }

    /// <summary>
    /// Sends an empty line to the console every second. The worldserver console loop flushes stdout each
    /// time it reads a line, so this makes buffered logs show up within a second; empty lines are ignored.
    /// </summary>
    internal bool SendFlushHeartbeat { get; init; }

    public ServerState State { get; private set; } = ServerState.Stopped;

    public DateTime? StartedAt { get; private set; }

    /// <summary>
    /// Reads CPU and memory use of the running process; null when it is not running. CPU is measured since
    /// the previous call and spread over all cores (100% = every core busy, like Task Manager), so the first
    /// call after a start has no CPU value yet. Call it from one thread at a steady interval.
    /// </summary>
    public ProcessMetrics? SampleMetrics()
    {
        Process? process = _process;
        if (process is null)
        {
            return null;
        }

        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return null;
            }

            TimeSpan cpuTime = process.TotalProcessorTime;
            DateTime now = DateTime.UtcNow;
            double? cpuPercent = null;

            if (_sampledPid == process.Id)
            {
                double elapsedMs = (now - _sampledAtUtc).TotalMilliseconds;
                if (elapsedMs > 0)
                {
                    double busyMs = (cpuTime - _sampledCpuTime).TotalMilliseconds;
                    cpuPercent = Math.Clamp(busyMs / (elapsedMs * Environment.ProcessorCount) * 100, 0, 100);
                }
            }

            _sampledPid = process.Id;
            _sampledCpuTime = cpuTime;
            _sampledAtUtc = now;

            return new ProcessMetrics(cpuPercent, process.WorkingSet64, process.PeakWorkingSet64);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process exited (or was disposed) while it was being read.
            return null;
        }
    }

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
            throw new FileNotFoundException(L.F("Srv.ExeNotFound", Name), ExePath);
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

        LogFileInfo? logFile = LogFileConf is null ? null : LogFileLocator.Find(ExePath, LogFileConf);
        _useConsoleOutput = logFile is null;
        AddLogLevelPrefix(startInfo, logFile);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnConsoleOutput(e.Data, false);
        process.ErrorDataReceived += (_, e) => OnConsoleOutput(e.Data, true);
        process.Exited += OnProcessExited;

        lock (_lock)
        {
            _stopRequested = false;
            _exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _process = process;
        }

        SetState(ServerState.Starting);
        DateTime startUtc = DateTime.UtcNow;

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
            Report(L.F("Srv.JobWarning", ex.Message), true);
        }

        StartedAt = DateTime.Now;

        // The console streams are always read, even when logs come from the file: a full pipe would block the server.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Report(L.F("Srv.Started", Name, process.Id), false);

        lock (_lock)
        {
            if (logFile is not null)
            {
                Report(L.F("Srv.ReadingLog", logFile.Path), false);
                _tailer = new LogFileTailer(logFile.Path, startUtc, logFile.TruncatedOnStart,
                    line => OnOutput(line, false));
            }

            if (SendFlushHeartbeat)
            {
                _heartbeat = new Timer(_ => SendHeartbeat(), null, HeartbeatInterval, HeartbeatInterval);
            }
        }

        if (ReadyMarker is null)
        {
            SetState(ServerState.Running);
        }
    }

    private void AddLogLevelPrefix(ProcessStartInfo startInfo, LogFileInfo? logFile)
    {
        try
        {
            string? confPath = logFile?.ConfPath ?? (ConsoleConf is null ? null : ConfFile.Find(ExePath, ConsoleConf));
            if (confPath is not null)
            {
                LogLevelPrefix.Apply(startInfo, confPath, logFile?.AppenderName ?? "Console");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the prefix, the level is guessed from the stream and the text.
        }
    }

    public void SendCommand(string command)
    {
        Process? process = _process;
        if (process is null || !IsRunning)
        {
            throw new InvalidOperationException(L.F("Srv.NotRunning", Name));
        }

        WriteInput(process, command + "\n");
    }

    private void SendHeartbeat()
    {
        Process? process = _process;
        if (process is null || State != ServerState.Running)
        {
            return;
        }

        try
        {
            WriteInput(process, "\n");
        }
        catch (Exception)
        {
            // The process is exiting and its input pipe is closed.
        }
    }

    private void WriteInput(Process process, string text)
    {
        lock (_stdinLock)
        {
            process.StandardInput.Write(text);
            process.StandardInput.Flush();
        }
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
            Report(L.F("Srv.Sending", ShutdownCommand), false);
            try
            {
                SendCommand(ShutdownCommand);
            }
            catch (Exception ex)
            {
                Report(L.F("Srv.SendShutdownFailed", ex.Message), true);
            }

            if (await Task.WhenAny(exitTask, Task.Delay(timeout)) == exitTask)
            {
                return;
            }

            Report(L.F("Srv.ForceKill", Name, timeout.TotalSeconds.ToString("0")), true);
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

    private void OnConsoleOutput(string? line, bool isError)
    {
        if (_useConsoleOutput)
        {
            OnOutput(line, isError);
        }
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
        LogFileTailer? tailer;
        Timer? heartbeat;

        lock (_lock)
        {
            if (!ReferenceEquals(_process, process))
            {
                process.Dispose();
                return;
            }

            stopRequested = _stopRequested;
            exitTcs = _exitTcs;
            tailer = _tailer;
            heartbeat = _heartbeat;
            _process = null;
            _tailer = null;
            _heartbeat = null;
        }

        heartbeat?.Dispose();
        tailer?.Dispose();
        process.Dispose();
        StartedAt = null;

        bool unexpected = !stopRequested && exitCode != 0 && exitCode != ServerManager.RestartExitCode;
        Report(L.F("Srv.Exited", Name, exitCode), unexpected);
        SetState(ServerState.Stopped);
        exitTcs?.TrySetResult(exitCode);
        Exited?.Invoke(this, exitCode, stopRequested);
    }

    /// <summary>Writes a panel message into this server's log.</summary>
    internal void Report(string message, bool isError) =>
        OutputReceived?.Invoke(this, L.Log(message), isError);

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
