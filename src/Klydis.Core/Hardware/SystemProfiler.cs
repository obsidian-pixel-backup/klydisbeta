using System;
using System.Management; // Note: Requires the 'System.Management' NuGet package.
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Hardware;

/// <summary>
/// Contains information about the System CPU and RAM.
/// </summary>
public record SystemInfo(
    string CpuName,
    int CoreCount,
    int LogicalProcessorCount,
    int ClockSpeedMHz,
    double TotalRamGb,
    double AvailableRamGb,
    double CpuUsagePercent);

/// <summary>
/// A comprehensive hardware profile combining System and GPU information.
/// </summary>
public record HardwareProfile(
    SystemInfo System,
    GpuInfo? Gpu);

/// <summary>
/// Profiles System Hardware (CPU and RAM) via Windows Management Instrumentation (WMI).
/// </summary>
public class SystemProfiler
{
    private readonly ILogger<SystemProfiler>? _logger;
    private readonly GpuProfiler _gpuProfiler;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemProfiler"/> class.
    /// </summary>
    /// <param name="gpuProfiler">The GPU profiler to combine info with.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public SystemProfiler(GpuProfiler gpuProfiler, ILogger<SystemProfiler>? logger = null)
    {
        _gpuProfiler = gpuProfiler ?? throw new ArgumentNullException(nameof(gpuProfiler));
        _logger = logger;
    }

    /// <summary>
    /// Queries the system CPU and RAM information asynchronously.
    /// </summary>
    /// <returns>A <see cref="SystemInfo"/> record.</returns>
    public Task<SystemInfo> GetSystemInfoAsync()
    {
        // WMI calls can be slow and synchronous, so we offload them to a background thread.
        return Task.Run(() =>
        {
            string cpuName = "Unknown CPU";
            int coreCount = 0;
            int logicalProcessors = 0;
            int clockSpeed = 0;
            double totalRamGb = 0;
            double availableRamGb = 0;
            double cpuUsagePercent = 0;

            try
            {
                using var processorSearcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, LoadPercentage FROM Win32_Processor");
                foreach (var obj in processorSearcher.Get())
                {
                    cpuName = obj["Name"]?.ToString() ?? cpuName;
                    coreCount = Convert.ToInt32(obj["NumberOfCores"] ?? 0);
                    logicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);
                    clockSpeed = Convert.ToInt32(obj["MaxClockSpeed"] ?? 0);
                    cpuUsagePercent = Convert.ToDouble(obj["LoadPercentage"] ?? 0);
                    break; // Just grab the first CPU
                }

                using var computerSystemSearcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in computerSystemSearcher.Get())
                {
                    ulong totalRamBytes = Convert.ToUInt64(obj["TotalPhysicalMemory"] ?? 0);
                    totalRamGb = totalRamBytes / (1024.0 * 1024.0 * 1024.0);
                    break;
                }

                using var osSearcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
                foreach (var obj in osSearcher.Get())
                {
                    ulong freeRamKb = Convert.ToUInt64(obj["FreePhysicalMemory"] ?? 0);
                    availableRamGb = freeRamKb / (1024.0 * 1024.0);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to query system information via WMI.");
            }

            return new SystemInfo(
                CpuName: cpuName,
                CoreCount: coreCount,
                LogicalProcessorCount: logicalProcessors,
                ClockSpeedMHz: clockSpeed,
                TotalRamGb: Math.Round(totalRamGb, 2),
                AvailableRamGb: Math.Round(availableRamGb, 2),
                CpuUsagePercent: Math.Round(cpuUsagePercent, 2)
            );
        });
    }

    /// <summary>
    /// Retrieves a combined profile of both the system and the GPU.
    /// </summary>
    /// <returns>A <see cref="HardwareProfile"/> instance.</returns>
    public async Task<HardwareProfile> GetHardwareProfileAsync()
    {
        var systemInfoTask = GetSystemInfoAsync();
        var gpuInfoTask = _gpuProfiler.GetGpuInfoAsync();

        await Task.WhenAll(systemInfoTask, gpuInfoTask);

        return new HardwareProfile(
            System: systemInfoTask.Result,
            Gpu: gpuInfoTask.Result
        );
    }
}
