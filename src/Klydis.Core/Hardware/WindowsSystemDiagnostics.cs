using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1416 // Windows platform specific

namespace Klydis.Core.Hardware;

/// <summary>
/// Native Windows implementation of <see cref="ISystemDiagnostics"/> providing authoritative,
/// structured hardware telemetry with automatic multi-tiered fallbacks.
/// </summary>
public sealed class WindowsSystemDiagnostics : ISystemDiagnostics
{
    private readonly GpuProfiler _gpuProfiler;
    private readonly ILogger<WindowsSystemDiagnostics>? _logger;

    private static readonly object _cpuSampleLock = new();
    private static ulong _lastIdleTime;
    private static ulong _lastKernelTime;
    private static ulong _lastUserTime;
    private static DateTime _lastSampleTimeUtc = DateTime.MinValue;
    private static double _lastCalculatedCpuUsage = 0.0;

    public WindowsSystemDiagnostics(GpuProfiler? gpuProfiler = null, ILogger<WindowsSystemDiagnostics>? logger = null)
    {
        _gpuProfiler = gpuProfiler ?? new GpuProfiler();
        _logger = logger;
    }

    public async Task<CpuInfoResult> GetCpuInfoAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            string model = "Unknown CPU";
            int physicalCores = Environment.ProcessorCount / 2;
            if (physicalCores < 1) physicalCores = 1;
            int logicalProcessors = Environment.ProcessorCount;
            int sockets = 1;
            int maxClock = 0;
            string arch = RuntimeInformation.ProcessArchitecture.ToString();

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation FROM Win32_Processor");
                foreach (var obj in searcher.Get())
                {
                    model = obj["Name"]?.ToString()?.Trim() ?? model;
                    if (obj["NumberOfCores"] != null) physicalCores = Convert.ToInt32(obj["NumberOfCores"]);
                    if (obj["NumberOfLogicalProcessors"] != null) logicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                    if (obj["MaxClockSpeed"] != null) maxClock = Convert.ToInt32(obj["MaxClockSpeed"]);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "WMI CPU info query failed, falling back to environment values.");
                string? envCpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                if (!string.IsNullOrWhiteSpace(envCpu)) model = envCpu;
            }

            return new CpuInfoResult(
                Model: model,
                Sockets: sockets,
                PhysicalCores: physicalCores,
                LogicalProcessors: logicalProcessors,
                MaxClockSpeedMHz: maxClock,
                Architecture: arch,
                Features: $"{arch}, {logicalProcessors} threads");
        }, ct).ConfigureAwait(false);
    }

    public async Task<CpuUsageResult> GetCpuUsageAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            double cpuUsage = SampleCpuUsage();
            double processCpu = SampleProcessCpu();

            return new CpuUsageResult(
                TotalUtilizationPercent: Math.Round(cpuUsage, 1),
                ProcessCpuPercent: Math.Round(processCpu, 1));
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GpuInfoResult>> GetGpuInfoAsync(CancellationToken ct = default)
    {
        var results = new List<GpuInfoResult>();

        // 1. Try GpuProfiler (NVML / nvidia-smi)
        try
        {
            var nvGpu = await _gpuProfiler.GetGpuInfoAsync().ConfigureAwait(false);
            if (nvGpu != null && !string.IsNullOrWhiteSpace(nvGpu.Name))
            {
                results.Add(new GpuInfoResult(
                    Model: nvGpu.Name,
                    TotalVramMb: nvGpu.TotalVramMb,
                    FreeVramMb: nvGpu.FreeVramMb,
                    UsedVramMb: nvGpu.UsedVramMb,
                    DriverVersion: nvGpu.DriverVersion,
                    ComputeCapability: nvGpu.ComputeCapability,
                    IsNvidia: true));
                return results;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "NVML GPU profiling failed; falling back to WMI video controllers.");
        }

        // 2. Fallback to WMI Win32_VideoController
        try
        {
            await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? "Generic Video Controller";
                    double vramMb = 0;
                    if (obj["AdapterRAM"] != null)
                    {
                        ulong ramBytes = Convert.ToUInt64(obj["AdapterRAM"]);
                        vramMb = ramBytes / (1024.0 * 1024.0);
                    }
                    string? driver = obj["DriverVersion"]?.ToString();
                    bool isNvidia = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

                    results.Add(new GpuInfoResult(
                        Model: name,
                        TotalVramMb: Math.Round(vramMb, 0),
                        FreeVramMb: 0,
                        UsedVramMb: 0,
                        DriverVersion: driver,
                        IsNvidia: isNvidia));
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WMI Video Controller query failed.");
        }

        if (results.Count == 0)
        {
            results.Add(new GpuInfoResult(
                Model: "Standard Display Adapter",
                TotalVramMb: 0,
                FreeVramMb: 0,
                UsedVramMb: 0,
                IsNvidia: false));
        }

        return results;
    }

    public async Task<GpuUsageResult?> GetGpuUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var nvGpu = await _gpuProfiler.GetGpuInfoAsync().ConfigureAwait(false);
            if (nvGpu != null)
            {
                double memPct = nvGpu.TotalVramMb > 0
                    ? (nvGpu.UsedVramMb / (double)nvGpu.TotalVramMb) * 100.0
                    : 0.0;

                return new GpuUsageResult(
                    UtilizationPercent: nvGpu.GpuUtilPercent,
                    MemoryUtilizationPercent: Math.Round(memPct, 1),
                    TemperatureCelsius: nvGpu.Temperature);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to retrieve GPU usage.");
        }

        return null;
    }

    public async Task<MemoryInfoResult> GetMemoryAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            // Try native GlobalMemoryStatusEx first
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                    double usedGb = Math.Max(0, totalGb - availGb);
                    double pct = totalGb > 0 ? (usedGb / totalGb) * 100.0 : 0.0;
                    double workingSetMb = Environment.WorkingSet / (1024.0 * 1024.0);

                    return new MemoryInfoResult(
                        TotalPhysicalGb: Math.Round(totalGb, 2),
                        AvailableGb: Math.Round(availGb, 2),
                        UsedGb: Math.Round(usedGb, 2),
                        UtilizationPercent: Math.Round(pct, 1),
                        ProcessWorkingSetMb: Math.Round(workingSetMb, 1));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "GlobalMemoryStatusEx failed; using WMI fallback.");
            }

            // Fallback: WMI
            double wmiTotalGb = 0;
            double wmiAvailGb = 0;
            try
            {
                using (var s1 = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (var o in s1.Get())
                    {
                        wmiTotalGb = Convert.ToUInt64(o["TotalPhysicalMemory"] ?? 0) / (1024.0 * 1024 * 1024);
                        break;
                    }
                }
                using (var s2 = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (var o in s2.Get())
                    {
                        wmiAvailGb = Convert.ToUInt64(o["FreePhysicalMemory"] ?? 0) / (1024.0 * 1024);
                        break;
                    }
                }
            }
            catch { }

            double fallbackUsed = Math.Max(0, wmiTotalGb - wmiAvailGb);
            double fallbackPct = wmiTotalGb > 0 ? (fallbackUsed / wmiTotalGb) * 100.0 : 0.0;

            return new MemoryInfoResult(
                TotalPhysicalGb: Math.Round(wmiTotalGb, 2),
                AvailableGb: Math.Round(wmiAvailGb, 2),
                UsedGb: Math.Round(fallbackUsed, 2),
                UtilizationPercent: Math.Round(fallbackPct, 1),
                ProcessWorkingSetMb: Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1));
        }, ct).ConfigureAwait(false);
    }

    public async Task<DiskInfoResult> GetDisksAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var driveItems = new List<DiskDriveItem>();
            double totalCapacity = 0;
            double totalFree = 0;

            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
                foreach (var d in drives)
                {
                    double totalGb = d.TotalSize / (1024.0 * 1024 * 1024);
                    double freeGb = d.TotalFreeSpace / (1024.0 * 1024 * 1024);
                    double usedGb = Math.Max(0, totalGb - freeGb);
                    double pct = totalGb > 0 ? (usedGb / totalGb) * 100.0 : 0.0;

                    totalCapacity += totalGb;
                    totalFree += freeGb;

                    driveItems.Add(new DiskDriveItem(
                        Name: d.Name,
                        VolumeLabel: string.IsNullOrWhiteSpace(d.VolumeLabel) ? "(No label)" : d.VolumeLabel,
                        Format: d.DriveFormat,
                        TotalGb: Math.Round(totalGb, 1),
                        FreeGb: Math.Round(freeGb, 1),
                        UsedGb: Math.Round(usedGb, 1),
                        UtilizationPercent: Math.Round(pct, 1)));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "DriveInfo.GetDrives failed.");
            }

            return new DiskInfoResult(
                Drives: driveItems,
                TotalCapacityGb: Math.Round(totalCapacity, 1),
                TotalFreeGb: Math.Round(totalFree, 1));
        }, ct).ConfigureAwait(false);
    }

    public async Task<OsInfoResult> GetOperatingSystemAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            string osName = Environment.OSVersion.ToString();
            string version = Environment.OSVersion.Version.ToString();
            string build = Environment.OSVersion.Version.Build.ToString();
            string arch = RuntimeInformation.ProcessArchitecture.ToString();
            string machineName = Environment.MachineName;
            string userName = Environment.UserName;
            DateTime? installDate = null;

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, InstallDate FROM Win32_OperatingSystem");
                foreach (var obj in searcher.Get())
                {
                    if (obj["Caption"] != null) osName = obj["Caption"].ToString()!.Trim();
                    if (obj["Version"] != null) version = obj["Version"].ToString()!.Trim();
                    if (obj["BuildNumber"] != null) build = obj["BuildNumber"].ToString()!.Trim();
                    if (obj["InstallDate"] != null)
                    {
                        string rawDate = obj["InstallDate"].ToString()!;
                        if (ManagementDateTimeConverter.ToDateTime(rawDate) is DateTime dt)
                        {
                            installDate = dt.ToUniversalTime();
                        }
                    }
                    break;
                }
            }
            catch { }

            return new OsInfoResult(
                OsName: osName,
                Version: version,
                BuildNumber: build,
                Architecture: arch,
                MachineName: machineName,
                UserName: userName,
                InstallDateUtc: installDate);
        }, ct).ConfigureAwait(false);
    }

    public async Task<TemperatureResult> GetTemperaturesAsync(CancellationToken ct = default)
    {
        double? gpuTemp = null;
        double? cpuTemp = null;
        var readings = new Dictionary<string, double>();

        try
        {
            var nvGpu = await _gpuProfiler.GetGpuInfoAsync().ConfigureAwait(false);
            if (nvGpu != null && nvGpu.Temperature > 0)
            {
                gpuTemp = nvGpu.Temperature;
                readings["GPU Core"] = nvGpu.Temperature;
            }
        }
        catch { }

        // ThermalZone thermal probe via WMI
        try
        {
            await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (var obj in searcher.Get())
                {
                    if (obj["CurrentTemperature"] != null)
                    {
                        // Stored in tenths of Kelvin
                        double raw = Convert.ToDouble(obj["CurrentTemperature"]);
                        double celsius = (raw / 10.0) - 273.15;
                        if (celsius is > 0 and < 120)
                        {
                            cpuTemp = Math.Round(celsius, 1);
                            readings["CPU Thermal Zone"] = cpuTemp.Value;
                            break;
                        }
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        catch { }

        return new TemperatureResult(
            CpuTemperatureCelsius: cpuTemp,
            GpuTemperatureCelsius: gpuTemp,
            SensorReadings: readings);
    }

    public async Task<ProcessInfoResult> GetProcessesAsync(int topN = 25, string? filter = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var allProcs = Process.GetProcesses();
            int total = allProcs.Length;

            var filtered = allProcs.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                filtered = filtered.Where(p => p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            var items = new List<ProcessItem>();
            foreach (var p in filtered)
            {
                try
                {
                    double wsMb = p.WorkingSet64 / (1024.0 * 1024.0);
                    items.Add(new ProcessItem(
                        Pid: p.Id,
                        Name: p.ProcessName,
                        WorkingSetMb: Math.Round(wsMb, 1),
                        MainWindowTitle: string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : p.MainWindowTitle));
                }
                catch
                {
                    // Process exited or permission denied
                }
            }

            var sorted = items
                .OrderByDescending(p => p.WorkingSetMb)
                .Take(Math.Clamp(topN, 1, 100))
                .ToList();

            return new ProcessInfoResult(
                TotalProcessCount: total,
                TopProcesses: sorted);
        }, ct).ConfigureAwait(false);
    }

    public async Task<GpuProcessInfoResult> GetGpuProcessesAsync(CancellationToken ct = default)
    {
        var items = new List<GpuProcessItem>();

        // Query via nvidia-smi if available
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[0].Trim(), out int pid) &&
                        double.TryParse(parts[2].Trim(), out double memMb))
                    {
                        items.Add(new GpuProcessItem(pid, parts[1].Trim(), memMb));
                    }
                }
            }
        }
        catch { }

        return new GpuProcessInfoResult(items);
    }

    public Task<UptimeResult> GetUptimeAsync(CancellationToken ct = default)
    {
        long tickCount = Environment.TickCount64;
        var uptime = TimeSpan.FromMilliseconds(tickCount);
        var bootTime = DateTime.UtcNow - uptime;

        string formatted = $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";

        return Task.FromResult(new UptimeResult(
            Uptime: uptime,
            SystemBootTimeUtc: bootTime,
            FormattedUptime: formatted));
    }

    public async Task<HardwareReportResult> GetHardwareReportAsync(CancellationToken ct = default)
    {
        var cpuTask = GetCpuInfoAsync(ct);
        var cpuUsageTask = GetCpuUsageAsync(ct);
        var gpusTask = GetGpuInfoAsync(ct);
        var memTask = GetMemoryAsync(ct);
        var disksTask = GetDisksAsync(ct);
        var tempsTask = GetTemperaturesAsync(ct);

        await Task.WhenAll(cpuTask, cpuUsageTask, gpusTask, memTask, disksTask, tempsTask).ConfigureAwait(false);

        return new HardwareReportResult(
            Cpu: await cpuTask,
            CpuUsage: await cpuUsageTask,
            Gpus: await gpusTask,
            Memory: await memTask,
            Disks: await disksTask,
            Temperatures: await tempsTask);
    }

    public async Task<SoftwareReportResult> GetSoftwareReportAsync(CancellationToken ct = default)
    {
        var osTask = GetOperatingSystemAsync(ct);
        var uptimeTask = GetUptimeAsync(ct);
        var procsTask = GetProcessesAsync(15, null, ct);

        await Task.WhenAll(osTask, uptimeTask, procsTask).ConfigureAwait(false);

        var runtimes = new List<string>
        {
            $".NET Runtime: {RuntimeInformation.FrameworkDescription}",
            $"OS Architecture: {RuntimeInformation.OSArchitecture}",
            $"Process Architecture: {RuntimeInformation.ProcessArchitecture}"
        };

        return new SoftwareReportResult(
            OperatingSystem: await osTask,
            Uptime: await uptimeTask,
            Processes: await procsTask,
            InstalledRuntimes: runtimes);
    }

    private static double SampleCpuUsage()
    {
        lock (_cpuSampleLock)
        {
            try
            {
                if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
                {
                    ulong idle = ToUInt64(idleFt);
                    ulong kernel = ToUInt64(kernelFt);
                    ulong user = ToUInt64(userFt);

                    if (_lastSampleTimeUtc == DateTime.MinValue)
                    {
                        _lastIdleTime = idle;
                        _lastKernelTime = kernel;
                        _lastUserTime = user;
                        _lastSampleTimeUtc = DateTime.UtcNow;

                        // First sample: query WMI as fallback or return estimated value
                        return GetWmiCpuFallback();
                    }

                    ulong idleDelta = idle - _lastIdleTime;
                    ulong kernelDelta = kernel - _lastKernelTime;
                    ulong userDelta = user - _lastUserTime;

                    _lastIdleTime = idle;
                    _lastKernelTime = kernel;
                    _lastUserTime = user;
                    _lastSampleTimeUtc = DateTime.UtcNow;

                    ulong totalDelta = kernelDelta + userDelta;
                    if (totalDelta > 0 && totalDelta >= idleDelta)
                    {
                        double busy = totalDelta - idleDelta;
                        double usage = (busy / (double)totalDelta) * 100.0;
                        _lastCalculatedCpuUsage = Math.Clamp(usage, 0.0, 100.0);
                        return _lastCalculatedCpuUsage;
                    }
                }
            }
            catch { }

            return GetWmiCpuFallback();
        }
    }

    private static double GetWmiCpuFallback()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
            foreach (var o in searcher.Get())
            {
                if (o["LoadPercentage"] != null)
                {
                    return Convert.ToDouble(o["LoadPercentage"]);
                }
            }
        }
        catch { }
        return _lastCalculatedCpuUsage > 0 ? _lastCalculatedCpuUsage : 5.0;
    }

    private static double SampleProcessCpu()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            return Math.Round(proc.TotalProcessorTime.TotalMilliseconds / (Environment.ProcessorCount * 1000.0) % 100.0, 1);
        }
        catch
        {
            return 0.0;
        }
    }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        return ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
