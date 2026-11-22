using System.Diagnostics;
using System.IO;

using AzerothCorePanel.Localization;

namespace AzerothCorePanel.Services;

/// <summary>
/// Starts, stops and restarts authserver and worldserver together, and restarts a server that crashed.
/// </summary>
public sealed class ServerManager : IDisposable
{
    // Exit codes from src/server/game/World/World.h (ShutdownExitCode).
    public const int ShutdownExitCode = 0;
    public const int ErrorExitCode = 1;
    public const int RestartExitCode = 2;

    private readonly JobObject _job = new();
    private readonly object _restartLock = new();
    private readonly Dictionary<ServerProcess, List<DateTime>> _crashRestarts = new();
    private readonly Dictionary<ServerProcess, CancellationTokenSource> _pendingRestarts = new();
    private bool _disposed;

    public ServerManager()
    {
        Auth = new ServerProcess("Authserver", _job, shutdownCommand: null, readyMarker: null)
        {
            LogFileConf = "authserver.conf",
        };

        // "server shutdown 0" is rejected by AzerothCore (the delay must be > 0).
        World = new ServerProcess("Worldserver", _job, shutdownCommand: "server shutdown 1",
            readyMarker: "(worldserver-daemon) ready...")
        {
            SendFlushHeartbeat = true,
        };

        Auth.Exited += OnServerExited;
        World.Exited += OnServerExited;
    }

    public ServerProcess Auth { get; }

    public ServerProcess World { get; }

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Restart a server that closed without being asked to.</summary>
    public bool AutoRestartOnCrash { get; set; } = true;

    /// <summary>How long to wait after a crash before starting the server again.</summary>
    public TimeSpan CrashRestartDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// At most this many crash restarts per server within <see cref="CrashRestartWindow"/>; after that the
    /// panel gives up, so a broken config or a database that is down does not cause an endless loop.
    /// </summary>
    public int CrashRestartLimit { get; set; } = 3;

    public TimeSpan CrashRestartWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Raised for events worth a desktop notification: (title, message, isError). Any thread.</summary>
    public event Action<string, string, bool>? Alert;

    public bool AnyRunning => Auth.IsRunning || World.IsRunning;

    public bool AllRunning => Auth.IsRunning && World.IsRunning;

    public bool AnyRestartPending
    {
        get
        {
            lock (_restartLock)
            {
                return _pendingRestarts.Count > 0;
            }
        }
    }

    public bool IsRestartPending(ServerProcess server)
    {
        lock (_restartLock)
        {
            return _pendingRestarts.ContainsKey(server);
        }
    }

    /// <summary>
    /// Returns the names of AzerothCore processes already running that this panel did not start, among
    /// <paramref name="servers"/> (both servers when none are given).
    /// </summary>
    public IReadOnlyList<string> FindForeignServers(params ServerProcess[] servers)
    {
        var found = new List<string>();

        foreach (ServerProcess server in servers.Length > 0 ? servers : new[] { Auth, World })
        {
            if (server.IsRunning)
            {
                continue;
            }

            string processName = Path.GetFileNameWithoutExtension(server.ExePath);
            Process[] running = Process.GetProcessesByName(processName);
            if (running.Length > 0)
            {
                found.Add($"{processName}.exe (PID {string.Join(", ", running.Select(p => p.Id))})");
            }

            foreach (Process process in running)
            {
                process.Dispose();
            }
        }

        return found;
    }

    /// <summary>Starts whichever servers are not running yet: authserver first, then worldserver.</summary>
    public void StartAll()
    {
        // A manual start replaces any pending crash restart and gives the servers a fresh restart budget.
        CancelPendingRestarts(clearHistory: true);
        Auth.Start();
        World.Start();
    }

    /// <summary>Stops worldserver first (so players are saved), then authserver.</summary>
    public async Task StopAllAsync()
    {
        CancelPendingRestarts(clearHistory: true);
        await World.StopAsync(ShutdownTimeout);
        await Auth.StopAsync(ShutdownTimeout);
    }

    public async Task RestartAllAsync()
    {
        await StopAllAsync();
        StartAll();
    }

    /// <summary>Starts one server, leaving the other as it is.</summary>
    public void StartServer(ServerProcess server)
    {
        CancelPendingRestart(server);
        server.Start();
    }

    /// <summary>Stops one server, leaving the other as it is (worldserver still saves the players).</summary>
    public async Task StopServerAsync(ServerProcess server)
    {
        CancelPendingRestart(server);
        await server.StopAsync(ShutdownTimeout);
    }

    /// <summary>Cancels a pending crash restart of one server and gives it a fresh restart budget.</summary>
    private void CancelPendingRestart(ServerProcess server)
    {
        lock (_restartLock)
        {
            if (_pendingRestarts.Remove(server, out CancellationTokenSource? cts))
            {
                cts.Cancel();
            }

            _crashRestarts.Remove(server);
        }
    }

    public void Dispose()
    {
        lock (_restartLock)
        {
            _disposed = true;
        }

        CancelPendingRestarts(clearHistory: true);
        _job.Dispose();
    }

    private void OnServerExited(ServerProcess server, int exitCode, bool stopRequested)
    {
        if (stopRequested)
        {
            return;
        }

        if (server == World && exitCode == RestartExitCode)
        {
            // Someone ran "server restart" in-game or in the console.
            server.Report(L.T("Srv.RestartRequested"), false);
            TryStart(server);
            return;
        }

        if (exitCode == ShutdownExitCode)
        {
            // A normal shutdown asked from inside the game or the console ("server shutdown").
            server.Report(L.F("Srv.ShutdownByCommand", server.Name), false);
            return;
        }

        string code = FormatExitCode(exitCode);

        if (!AutoRestartOnCrash)
        {
            server.Report(L.F("Srv.CrashedNoRestart", server.Name, code), true);
            Alert?.Invoke(L.F("Alert.Crashed", server.Name), L.T("Alert.AutoRestartOff"), true);
            return;
        }

        CancellationTokenSource cts;
        int attempt;

        lock (_restartLock)
        {
            if (_disposed)
            {
                return;
            }

            DateTime now = DateTime.Now;
            if (!_crashRestarts.TryGetValue(server, out List<DateTime>? history))
            {
                history = new List<DateTime>();
                _crashRestarts[server] = history;
            }

            history.RemoveAll(time => now - time > CrashRestartWindow);

            if (history.Count >= CrashRestartLimit)
            {
                history.Clear();
                server.Report(L.F("Srv.GaveUp", server.Name, code, CrashRestartLimit + 1,
                    CrashRestartWindow.TotalMinutes.ToString("0")), true);
                Alert?.Invoke(L.F("Alert.CrashedAgain", server.Name),
                    L.F("Alert.GaveUp", CrashRestartLimit + 1, CrashRestartWindow.TotalMinutes.ToString("0")), true);
                return;
            }

            history.Add(now);
            attempt = history.Count;

            cts = new CancellationTokenSource();
            _pendingRestarts[server] = cts;
        }

        server.Report(L.F("Srv.Restarting", server.Name, code, CrashRestartDelay.TotalSeconds.ToString("0"), attempt,
            CrashRestartLimit), true);
        Alert?.Invoke(L.F("Alert.Crashed", server.Name),
            L.F("Alert.Restarting", CrashRestartDelay.TotalSeconds.ToString("0"), attempt, CrashRestartLimit), true);

        _ = RestartAfterDelayAsync(server, cts);
    }

    private async Task RestartAfterDelayAsync(ServerProcess server, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(CrashRestartDelay, cts.Token);

            // On startup authserver marks every realm offline and refuses to start when no realm is left
            // (realmlist flag 3). A worldserver that is still loading has the version-mismatch flag set, so
            // starting authserver now would fail again; wait until worldserver finished loading.
            if (server == Auth && World.State == ServerState.Starting)
            {
                server.Report(L.T("Srv.WaitingWorld"), false);
                while (World.State == ServerState.Starting)
                {
                    await Task.Delay(500, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_restartLock)
        {
            if (_disposed || !_pendingRestarts.TryGetValue(server, out CancellationTokenSource? current)
                || current != cts)
            {
                return;
            }

            _pendingRestarts.Remove(server);
        }

        cts.Dispose();
        TryStart(server);
    }

    private void CancelPendingRestarts(bool clearHistory)
    {
        lock (_restartLock)
        {
            foreach (CancellationTokenSource cts in _pendingRestarts.Values)
            {
                cts.Cancel();
            }

            _pendingRestarts.Clear();

            if (clearHistory)
            {
                _crashRestarts.Clear();
            }
        }
    }

    private static void TryStart(ServerProcess server)
    {
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            server.Report(L.F("Srv.RestartFailed", ex.Message), true);
        }
    }

    /// <summary>Windows crash codes (such as 0xC0000005, access violation) read better in hex.</summary>
    internal static string FormatExitCode(int exitCode) =>
        exitCode < 0 ? $"0x{(uint)exitCode:X8}" : exitCode.ToString();
}
