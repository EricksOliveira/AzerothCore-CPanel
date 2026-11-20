namespace AzerothCorePanel.Services;

/// <summary>Resource use of a server process at one moment.</summary>
/// <param name="CpuPercent">CPU use over all cores, 0-100; null until two samples exist.</param>
/// <param name="WorkingSetBytes">Physical memory the process uses now (working set).</param>
/// <param name="PeakWorkingSetBytes">Highest working set since the process started.</param>
public readonly record struct ProcessMetrics(double? CpuPercent, long WorkingSetBytes, long PeakWorkingSetBytes);
