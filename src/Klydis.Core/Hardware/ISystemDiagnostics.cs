using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.Hardware;

public sealed record CpuInfoResult(
    string Model,
    int Sockets,
    int PhysicalCores,
    int LogicalProcessors,
    int MaxClockSpeedMHz,
    string Architecture,
    string? Features = null);

public sealed record CpuUsageResult(
    double TotalUtilizationPercent,
    IReadOnlyList<double>? PerCoreUtilizationPercent = null,
    double ProcessCpuPercent = 0.0);

public sealed record GpuInfoResult(
    string Model,
    double TotalVramMb,
    double FreeVramMb,
    double UsedVramMb,
    string? DriverVersion = null,
    string? ComputeCapability = null,
    bool IsNvidia = false);

public sealed record GpuUsageResult(
    double UtilizationPercent,
    double MemoryUtilizationPercent,
    double TemperatureCelsius,
    double PowerUsageWatts = 0.0);

public sealed record MemoryInfoResult(
    double TotalPhysicalGb,
    double AvailableGb,
    double UsedGb,
    double UtilizationPercent,
    double ProcessWorkingSetMb);

public sealed record DiskDriveItem(
    string Name,
    string VolumeLabel,
    string Format,
    double TotalGb,
    double FreeGb,
    double UsedGb,
    double UtilizationPercent);

public sealed record DiskInfoResult(
    IReadOnlyList<DiskDriveItem> Drives,
    double TotalCapacityGb,
    double TotalFreeGb);

public sealed record OsInfoResult(
    string OsName,
    string Version,
    string BuildNumber,
    string Architecture,
    string MachineName,
    string UserName,
    DateTime? InstallDateUtc = null);

public sealed record TemperatureResult(
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    IReadOnlyDictionary<string, double>? SensorReadings = null);

public sealed record ProcessItem(
    int Pid,
    string Name,
    double WorkingSetMb,
    double CpuPercent = 0.0,
    string? MainWindowTitle = null);

public sealed record ProcessInfoResult(
    int TotalProcessCount,
    IReadOnlyList<ProcessItem> TopProcesses);

public sealed record GpuProcessItem(
    int Pid,
    string Name,
    double UsedVramMb);

public sealed record GpuProcessInfoResult(
    IReadOnlyList<GpuProcessItem> GpuProcesses);

public sealed record UptimeResult(
    TimeSpan Uptime,
    DateTime SystemBootTimeUtc,
    string FormattedUptime);

public sealed record HardwareReportResult(
    CpuInfoResult Cpu,
    CpuUsageResult CpuUsage,
    IReadOnlyList<GpuInfoResult> Gpus,
    MemoryInfoResult Memory,
    DiskInfoResult Disks,
    TemperatureResult Temperatures);

public sealed record SoftwareReportResult(
    OsInfoResult OperatingSystem,
    UptimeResult Uptime,
    ProcessInfoResult Processes,
    IReadOnlyList<string>? InstalledRuntimes = null);

/// <summary>
/// Native typed system diagnostics interface. Provides high-reliability C# hardware and
/// system perception tools with deterministic multi-tiered fallbacks.
/// </summary>
public interface ISystemDiagnostics
{
    Task<CpuInfoResult> GetCpuInfoAsync(CancellationToken ct = default);
    Task<CpuUsageResult> GetCpuUsageAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GpuInfoResult>> GetGpuInfoAsync(CancellationToken ct = default);
    Task<GpuUsageResult?> GetGpuUsageAsync(CancellationToken ct = default);
    Task<MemoryInfoResult> GetMemoryAsync(CancellationToken ct = default);
    Task<DiskInfoResult> GetDisksAsync(CancellationToken ct = default);
    Task<OsInfoResult> GetOperatingSystemAsync(CancellationToken ct = default);
    Task<TemperatureResult> GetTemperaturesAsync(CancellationToken ct = default);
    Task<ProcessInfoResult> GetProcessesAsync(int topN = 25, string? filter = null, CancellationToken ct = default);
    Task<GpuProcessInfoResult> GetGpuProcessesAsync(CancellationToken ct = default);
    Task<UptimeResult> GetUptimeAsync(CancellationToken ct = default);
    Task<HardwareReportResult> GetHardwareReportAsync(CancellationToken ct = default);
    Task<SoftwareReportResult> GetSoftwareReportAsync(CancellationToken ct = default);
}
