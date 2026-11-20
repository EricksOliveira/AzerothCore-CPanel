using System.Net.Sockets;

namespace AzerothCorePanel.Services;

/// <summary>
/// Waits until MySQL accepts connections. Right after Windows starts, the MySQL service can still be
/// starting; servers started before it is ready fail to connect to the database.
/// </summary>
public static class DatabaseWaiter
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads host and port of LoginDatabaseInfo ("host;port;user;password;database") from worldserver.conf.
    /// Returns null when it cannot be read, or for "." (a named pipe, which has no TCP port to check).
    /// </summary>
    public static (string Host, int Port)? FindLoginDatabase(string worldserverExe)
    {
        string? confPath = ConfFile.Find(worldserverExe, "worldserver.conf");
        if (confPath is null)
        {
            return null;
        }

        if (!ConfFile.Read(confPath).TryGetValue("LoginDatabaseInfo", out string? info))
        {
            return null;
        }

        string[] parts = info.Split(';');
        if (parts.Length < 2 || parts[0] == "." || !int.TryParse(parts[1], out int port))
        {
            return null;
        }

        return (parts[0], port);
    }

    /// <summary>Returns true once a TCP connection to host:port succeeds, false after the timeout.</summary>
    public static async Task<bool> WaitAsync(string host, int port, TimeSpan timeout, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                using var client = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(host, port, attempt.Token);
                return true;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // Not accepting connections yet.
            }

            if (DateTime.UtcNow + RetryInterval > deadline)
            {
                return false;
            }

            await Task.Delay(RetryInterval, token);
        }
    }
}
