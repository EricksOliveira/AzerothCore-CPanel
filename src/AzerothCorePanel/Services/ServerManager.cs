using System.Diagnostics;
using System.IO;

namespace AzerothCorePanel.Services;

/// <summary>
/// Starts, stops and restarts authserver and worldserver together.
/// </summary>
public sealed class ServerManager : IDisposable
{
    // Exit codes from src/server/game/World/World.h (ShutdownExitCode).
    public const int ShutdownExitCode = 0;
    public const int ErrorExitCode = 1;
    public const int RestartExitCode = 2;

    private readonly JobObject _job = new();

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

        World.Exited += OnWorldExited;
    }

    public ServerProcess Auth { get; }

    public ServerProcess World { get; }

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public bool AnyRunning => Auth.IsRunning || World.IsRunning;

    public bool AllRunning => Auth.IsRunning && World.IsRunning;

    /// <summary>
    /// Returns the names of AzerothCore processes already running that this panel did not start.
    /// </summary>
    public IReadOnlyList<string> FindForeignServers()
    {
        var found = new List<string>();

        foreach (ServerProcess server in new[] { Auth, World })
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
        Auth.Start();
        World.Start();
    }

    /// <summary>Stops worldserver first (so players are saved), then authserver.</summary>
    public async Task StopAllAsync()
    {
        await World.StopAsync(ShutdownTimeout);
        await Auth.StopAsync(ShutdownTimeout);
    }

    public async Task RestartAllAsync()
    {
        await StopAllAsync();
        StartAll();
    }

    public void Dispose() => _job.Dispose();

    private void OnWorldExited(ServerProcess world, int exitCode, bool stopRequested)
    {
        if (stopRequested)
        {
            return;
        }

        if (exitCode == RestartExitCode)
        {
            // Someone ran "server restart" in-game or in the console.
            world.Report("Restart pedido pelo próprio worldserver; iniciando novamente...", false);
            try
            {
                world.Start();
            }
            catch (Exception ex)
            {
                world.Report($"Falha ao reiniciar: {ex.Message}", true);
            }
        }
        else if (exitCode != ShutdownExitCode)
        {
            world.Report("O worldserver fechou inesperadamente. Veja o Errors.log e os logs acima.", true);
        }
    }
}
