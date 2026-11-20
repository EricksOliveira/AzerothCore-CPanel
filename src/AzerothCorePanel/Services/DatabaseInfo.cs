using MySqlConnector;

namespace AzerothCorePanel.Services;

/// <summary>
/// One of the *DatabaseInfo settings of worldserver.conf: "host;port;user;password;database".
/// A host of "." means a named pipe, whose name is in the port field.
/// </summary>
public sealed record DatabaseInfo(string Host, string Port, string User, string Password, string Database)
{
    public bool IsNamedPipe => Host == ".";

    public static DatabaseInfo? Read(string worldserverExe, string settingName)
    {
        string? confPath = ConfFile.Find(worldserverExe, "worldserver.conf");
        if (confPath is null || !ConfFile.Read(confPath).TryGetValue(settingName, out string? value))
        {
            return null;
        }

        string[] parts = value.Split(';');
        return parts.Length < 5 ? null : new DatabaseInfo(parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    /// <param name="includeDatabase">False to connect to the server only (for example to run SHUTDOWN).</param>
    public string ToConnectionString(bool includeDatabase = true)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            UserID = User,
            Password = Password,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 5,
            // MySQL 8 without SSL needs this to log in with caching_sha2_password.
            AllowPublicKeyRetrieval = true,
        };

        if (includeDatabase)
        {
            builder.Database = Database;
        }

        if (IsNamedPipe)
        {
            builder.ConnectionProtocol = MySqlConnectionProtocol.NamedPipe;
            builder.PipeName = Port;
        }
        else
        {
            builder.Server = Host;
            if (uint.TryParse(Port, out uint port))
            {
                builder.Port = port;
            }
        }

        return builder.ConnectionString;
    }
}
