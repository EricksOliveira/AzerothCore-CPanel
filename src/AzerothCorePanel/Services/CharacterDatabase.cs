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

    /// <summary>Connection string from CharacterDatabaseInfo, or null when it cannot be read.</summary>
    public static string? BuildConnectionString(string worldserverExe) =>
        DatabaseInfo.Read(worldserverExe, "CharacterDatabaseInfo")?.ToConnectionString();

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
