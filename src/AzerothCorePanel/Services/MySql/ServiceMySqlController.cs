namespace AzerothCorePanel.Services.MySql;

/// <summary>MySQL or MariaDB installed as a Windows service.</summary>
public sealed class ServiceMySqlController : IMySqlController
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public ServiceMySqlController(string serviceName, string? executablePath = null)
    {
        ServiceName = serviceName;
        ExecutablePath = executablePath;
    }

    public string ServiceName { get; }

    /// <summary>The service's mysqld.exe / mariadbd.exe, when it could be read from the registry.</summary>
    public string? ExecutablePath { get; }

    public string Description => $"serviço {ServiceName}";

    public MySqlState GetState() => WindowsService.GetState(ServiceName) switch
    {
        ServiceState.Running => MySqlState.Running,
        ServiceState.Stopped => MySqlState.Stopped,
        ServiceState.StartPending or ServiceState.ContinuePending => MySqlState.Starting,
        ServiceState.StopPending or ServiceState.PausePending => MySqlState.Stopping,
        _ => MySqlState.Unknown,
    };

    public Task StartAsync(CancellationToken token) => WindowsService.StartAsync(ServiceName, Timeout, token);

    public Task StopAsync(CancellationToken token) => WindowsService.StopAsync(ServiceName, Timeout, token);
}
