using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AzerothCorePanel.Localization;

namespace AzerothCorePanel.Services.MySql;

public enum ServiceState
{
    NotFound = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

/// <summary>
/// Minimal Windows service control through advapi32, so no extra package is needed. Reading the state works
/// for any user; starting and stopping usually needs administrator rights, so on "access denied" the action
/// is repeated through sc.exe with a UAC prompt.
/// </summary>
internal static class WindowsService
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 1;

    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorCancelled = 1223;

    public static ServiceState GetState(string serviceName)
    {
        IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return ServiceState.NotFound;
        }

        try
        {
            IntPtr service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return ServiceState.NotFound;
            }

            try
            {
                return QueryServiceStatus(service, out SERVICE_STATUS status)
                    ? (ServiceState)status.CurrentState
                    : ServiceState.NotFound;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static async Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken token)
    {
        int error = Control(serviceName, ServiceStart, service => StartService(service, 0, null));

        if (error == ErrorAccessDenied)
        {
            await RunElevatedAsync("start", serviceName, token);
        }
        else if (error != 0 && error != ErrorServiceAlreadyRunning)
        {
            throw new Win32Exception(error);
        }

        await WaitForStateAsync(serviceName, ServiceState.Running, timeout, token);
    }

    public static async Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken token)
    {
        int error = Control(serviceName, ServiceStop,
            service => ControlService(service, ServiceControlStop, out _));

        if (error == ErrorAccessDenied)
        {
            await RunElevatedAsync("stop", serviceName, token);
        }
        else if (error != 0 && error != ErrorServiceNotActive)
        {
            throw new Win32Exception(error);
        }

        await WaitForStateAsync(serviceName, ServiceState.Stopped, timeout, token);
    }

    /// <summary>Opens the service with the given access and runs the action; returns the Win32 error (0 = ok).</summary>
    private static int Control(string serviceName, uint access, Func<IntPtr, bool> action)
    {
        IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error();
        }

        try
        {
            IntPtr service = OpenService(manager, serviceName, access);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error();
            }

            try
            {
                return action(service) ? 0 : Marshal.GetLastWin32Error();
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static async Task RunElevatedAsync(string verb, string serviceName, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo("sc.exe", $"{verb} \"{serviceName}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync(token);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new InvalidOperationException(L.T("Svc.AdminDenied"), ex);
        }
    }

    private static async Task WaitForStateAsync(
        string serviceName, ServiceState wanted, TimeSpan timeout, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        ServiceState state;

        while ((state = GetState(serviceName)) != wanted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(L.F("Svc.Timeout", serviceName, Describe(wanted), timeout.TotalSeconds.ToString("0"),
                    Describe(state)));
            }

            await Task.Delay(500, token);
        }
    }

    public static string Describe(ServiceState state) => state switch
    {
        ServiceState.Running => L.T("Svc.Running"),
        ServiceState.Stopped => L.T("Svc.Stopped"),
        ServiceState.StartPending => L.T("Svc.Starting"),
        ServiceState.StopPending => L.T("Svc.Stopping"),
        ServiceState.Paused => L.T("Svc.Paused"),
        ServiceState.NotFound => L.T("Svc.NotFound"),
        _ => state.ToString(),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartServiceW")]
    private static extern bool StartService(IntPtr service, int argCount, string[]? args);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
