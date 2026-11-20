namespace AzerothCorePanel.Services.MySql;

public enum MySqlState
{
    Unknown,
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>A MySQL server the panel can start and stop.</summary>
public interface IMySqlController
{
    /// <summary>Short text for the status area, such as "serviço MySQL80".</summary>
    string Description { get; }

    /// <summary>Cheap enough to call every second.</summary>
    MySqlState GetState();

    /// <summary>Starts MySQL and returns once it is running (not necessarily accepting connections yet).</summary>
    Task StartAsync(CancellationToken token);

    /// <summary>Shuts MySQL down cleanly and returns once it stopped.</summary>
    Task StopAsync(CancellationToken token);
}
