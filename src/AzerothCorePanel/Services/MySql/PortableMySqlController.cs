using System.Diagnostics;
using System.IO;
using MySqlConnector;

namespace AzerothCorePanel.Services.MySql;

/// <summary>
/// A mysqld.exe shipped inside a Repack folder. It is started as a normal process and shut down with the
/// SHUTDOWN statement, using the LoginDatabaseInfo account from worldserver.conf.
/// </summary>
/// <remarks>
/// mysqld is deliberately not added to the panel's Job Object: if the panel closes or crashes, MySQL keeps
/// running instead of being killed in the middle of a write.
/// </remarks>
public sealed class PortableMySqlController : IMySqlController
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(90);

    private readonly Func<DatabaseInfo?> _account;
    private Process? _process;
    private bool _stopping;

    /// <param name="mysqldPath">Full path of mysqld.exe (or mariadbd.exe).</param>
    /// <param name="defaultsFile">my.ini to start with, or null for MySQL's own defaults.</param>
    /// <param name="account">Server address and an account allowed to run SHUTDOWN (normally root).</param>
    public PortableMySqlController(string mysqldPath, string? defaultsFile, Func<DatabaseInfo?> account)
    {
        MysqldPath = mysqldPath;
        DefaultsFile = defaultsFile;
        _account = account;
    }

    public string MysqldPath { get; }

    public string? DefaultsFile { get; }

    public string Description => $"portátil, {MysqldPath}";

    public MySqlState GetState()
    {
        Process? process = FindProcess();
        if (process is null)
        {
            return MySqlState.Stopped;
        }

        if (!ReferenceEquals(process, _process))
        {
            process.Dispose();
        }

        return _stopping ? MySqlState.Stopping : MySqlState.Running;
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (GetState() == MySqlState.Running)
        {
            return;
        }

        string baseDir = Path.GetDirectoryName(Path.GetDirectoryName(MysqldPath)!)!;
        var startInfo = new ProcessStartInfo(MysqldPath)
        {
            WorkingDirectory = baseDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // --defaults-file must be the first argument.
        if (DefaultsFile is not null)
        {
            startInfo.ArgumentList.Add($"--defaults-file={DefaultsFile}");
        }

        _process?.Dispose();
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("O mysqld não iniciou.");

        // Running means "accepting connections": wait for the port, and fail fast if mysqld exits.
        DatabaseInfo? account = _account();
        if (account is null || account.IsNamedPipe || !int.TryParse(account.Port, out int port))
        {
            return;
        }

        DateTime deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"O MySQL fechou ao iniciar (código {_process.ExitCode}). Veja o arquivo .err na pasta de dados.");
            }

            if (await DatabaseWaiter.WaitAsync(account.Host, port, TimeSpan.FromSeconds(2), token))
            {
                return;
            }
        }

        throw new TimeoutException($"O MySQL não aceitou conexões em {StartTimeout.TotalSeconds:0}s.");
    }

    public async Task StopAsync(CancellationToken token)
    {
        Process? process = FindProcess();
        if (process is null)
        {
            return;
        }

        DatabaseInfo account = _account()
            ?? throw new InvalidOperationException("Não encontrei o LoginDatabaseInfo no worldserver.conf.");

        _stopping = true;
        try
        {
            await using (var connection = new MySqlConnection(account.ToConnectionString(includeDatabase: false)))
            {
                await connection.OpenAsync(token);
                await using var command = new MySqlCommand("SHUTDOWN", connection);
                await command.ExecuteNonQueryAsync(token);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(StopTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"O MySQL não fechou em {StopTimeout.TotalSeconds:0}s.");
            }
        }
        finally
        {
            _stopping = false;
            if (!ReferenceEquals(process, _process))
            {
                process.Dispose();
            }
        }
    }

    /// <summary>The mysqld process of this exe: the one the panel started, or one started some other way.</summary>
    private Process? FindProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                return _process;
            }
        }
        catch (InvalidOperationException)
        {
        }

        string name = Path.GetFileNameWithoutExtension(MysqldPath);
        Process? found = null;

        foreach (Process candidate in Process.GetProcessesByName(name))
        {
            bool match = false;
            try
            {
                match = found is null && string.Equals(candidate.MainModule?.FileName, MysqldPath,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Another user's process (for example a service): not this portable MySQL.
            }

            if (match)
            {
                found = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }

        return found;
    }
}
