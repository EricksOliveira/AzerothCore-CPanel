using AzerothCorePanel.Models;
using MySqlConnector;

namespace AzerothCorePanel.Services;

/// <summary>
/// Reads online characters from the characters database configured in worldserver.conf.
/// </summary>
public static class CharacterDatabase
{
    private const string OnlinePlayersQuery =
        "SELECT name, level, race, class, map FROM characters WHERE online <> 0 ORDER BY name";

    /// <summary>
    /// Builds a connection string from CharacterDatabaseInfo ("host;port;user;password;database").
    /// A host of "." means a named pipe, whose name is in the port field. Returns null when it cannot be read.
    /// </summary>
    public static string? BuildConnectionString(string worldserverExe)
    {
        string? confPath = ConfFile.Find(worldserverExe, "worldserver.conf");
        if (confPath is null || !ConfFile.Read(confPath).TryGetValue("CharacterDatabaseInfo", out string? info))
        {
            return null;
        }

        string[] parts = info.Split(';');
        if (parts.Length < 5)
        {
            return null;
        }

        var builder = new MySqlConnectionStringBuilder
        {
            UserID = parts[2],
            Password = parts[3],
            Database = parts[4],
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 5,
            // MySQL 8 without SSL needs this to log in with caching_sha2_password.
            AllowPublicKeyRetrieval = true,
        };

        if (parts[0] == ".")
        {
            builder.ConnectionProtocol = MySqlConnectionProtocol.NamedPipe;
            builder.PipeName = parts[1];
        }
        else
        {
            builder.Server = parts[0];
            if (uint.TryParse(parts[1], out uint port))
            {
                builder.Port = port;
            }
        }

        return builder.ConnectionString;
    }

    public static async Task<IReadOnlyList<OnlinePlayer>> GetOnlinePlayersAsync(
        string connectionString, CancellationToken token)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(token);
        return await GetOnlinePlayersAsync(connection, null, token);
    }

    /// <summary>Runs the query on an open connection (optionally inside a transaction).</summary>
    public static async Task<IReadOnlyList<OnlinePlayer>> GetOnlinePlayersAsync(
        MySqlConnection connection, MySqlTransaction? transaction, CancellationToken token)
    {
        await using var command = new MySqlCommand(OnlinePlayersQuery, connection, transaction);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);

        var players = new List<OnlinePlayer>();
        while (await reader.ReadAsync(token))
        {
            players.Add(new OnlinePlayer(
                reader.GetString(0),
                Convert.ToInt32(reader.GetValue(1)),
                Convert.ToInt32(reader.GetValue(2)),
                Convert.ToInt32(reader.GetValue(3)),
                Convert.ToInt32(reader.GetValue(4))));
        }

        return players;
    }
}
