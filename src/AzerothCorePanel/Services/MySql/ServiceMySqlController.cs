namespace AzerothCorePanel.Services.MySql;

/// <summary>MySQL or MariaDB installed as a Windows service.</summary>
public sealed class ServiceMySqlController : IMySqlController
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public ServiceMySqlController(string serviceName)
    {
        ServiceName = serviceName;
    }

    public string ServiceName { get; }

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
