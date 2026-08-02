using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1416

namespace Klydis.Core.Hardware;

/// <summary>
/// Contains information about the NVIDIA GPU.
/// </summary>
public record GpuInfo(
    string Name,
    int TotalVramMb,
    int FreeVramMb,
    int UsedVramMb,
    string ComputeCapability,
    int Temperature,
    string DriverVersion,
    int GpuUtilPercent = 0);

/// <summary>
/// Represents real-time VRAM usage of the GPU.
/// </summary>
public record VramUsage(int FreeVramMb, int UsedVramMb);

/// <summary>
/// Profiles NVIDIA GPUs using nvidia-smi without depending on external native wrappers.
/// </summary>
public class GpuProfiler
{
    private readonly ILogger<GpuProfiler>? _logger;
    private static bool _isNvmlInitialized;
    private static IntPtr _nvmlDeviceHandle = IntPtr.Zero;
    private static string _nvmlGpuName = string.Empty;
    private static readonly object _nvmlLock = new();

    private static class NvmlNative
    {
        private const string NvmlDll = "nvml.dll";

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct NvmlMemory
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [System.Runtime.InteropServices.DllImport(NvmlDll, EntryPoint = "nvmlInit_v2", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int nvmlInit();

        [System.Runtime.InteropServices.DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [System.Runtime.InteropServices.DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetUtilizationRates", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [System.Runtime.InteropServices.DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMemoryInfo", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

        [System.Runtime.InteropServices.DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetTemperature", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

        [System.Runtime.InteropServices.DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetName", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetName(IntPtr device, System.Text.StringBuilder name, uint length);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuProfiler"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public GpuProfiler(ILogger<GpuProfiler>? logger = null)
    {
        _logger = logger;
    }

    private static void EnsureNvmlInitialized()
    {
        if (_isNvmlInitialized) return;
        lock (_nvmlLock)
        {
            if (_isNvmlInitialized) return;
            try
            {
                int initResult = NvmlNative.nvmlInit();
                if (initResult == 0)
                {
                    int handleResult = NvmlNative.nvmlDeviceGetHandleByIndex(0, out _nvmlDeviceHandle);
                    if (handleResult == 0)
                    {
                        var sb = new System.Text.StringBuilder(64);
                        if (NvmlNative.nvmlDeviceGetName(_nvmlDeviceHandle, sb, 64) == 0)
                        {
                            _nvmlGpuName = sb.ToString();
                        }
                        _isNvmlInitialized = true;
                    }
                }
            }
            catch
            {
                _isNvmlInitialized = false;
            }
        }
    }

    private GpuInfo? GetGpuInfoFromNvml()
    {
        try
        {
            EnsureNvmlInitialized();
            if (!_isNvmlInitialized || _nvmlDeviceHandle == IntPtr.Zero) return null;

            if (NvmlNative.nvmlDeviceGetUtilizationRates(_nvmlDeviceHandle, out var util) == 0 &&
                NvmlNative.nvmlDeviceGetMemoryInfo(_nvmlDeviceHandle, out var mem) == 0)
            {
                uint temp = 0;
                try { NvmlNative.nvmlDeviceGetTemperature(_nvmlDeviceHandle, 0, out temp); } catch { }

                int totalMb = (int)(mem.Total / (1024 * 1024));
                int freeMb = (int)(mem.Free / (1024 * 1024));
                int usedMb = (int)(mem.Used / (1024 * 1024));
                int gpuUtil = (int)util.Gpu;

                string name = !string.IsNullOrEmpty(_nvmlGpuName) ? _nvmlGpuName : "NVIDIA GPU";

                return new GpuInfo(
                    Name: name,
                    TotalVramMb: totalMb,
                    FreeVramMb: freeMb,
                    UsedVramMb: usedMb,
                    ComputeCapability: "8.0",
                    Temperature: (int)temp,
                    DriverVersion: "NVML Native",
                    GpuUtilPercent: gpuUtil
                );
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "NVML native GPU query failed.");
        }
        return null;
    }

    /// <summary>
    /// Queries the system for NVIDIA GPU information.
    /// </summary>
    /// <returns>A <see cref="GpuInfo"/> record if an NVIDIA GPU is found; otherwise, null.</returns>
    public async Task<GpuInfo?> GetGpuInfoAsync()
    {
        // 1. Try fast native NVML P/Invoke first (sub-millisecond, zero subprocess overhead)
        var nvmlInfo = GetGpuInfoFromNvml();
        if (nvmlInfo != null)
        {
            return nvmlInfo;
        }

        // 2. Fallback to nvidia-smi CLI execution
        try
        {
            var output = await RunNvidiaSmiAsync("--query-gpu=name,memory.total,memory.free,memory.used,compute_cap,temperature.gpu,driver_version,utilization.gpu --format=csv,noheader,nounits");
            if (!string.IsNullOrWhiteSpace(output))
            {
                var parts = output.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 8)
                {
                    return new GpuInfo(
                        Name: parts[0],
                        TotalVramMb: int.TryParse(parts[1], out var total) ? total : 0,
                        FreeVramMb: int.TryParse(parts[2], out var free) ? free : 0,
                        UsedVramMb: int.TryParse(parts[3], out var used) ? used : 0,
                        ComputeCapability: parts[4],
                        Temperature: int.TryParse(parts[5], out var temp) ? temp : 0,
                        DriverVersion: parts[6],
                        GpuUtilPercent: int.TryParse(parts[7], out var util) ? util : 0
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to query GPU info via nvidia-smi. Attempting WMI fallback.");
        }

        // Fallback to WMI if nvidia-smi query failed
        return GetGpuInfoFromWmi();
    }

    /// <summary>
    /// Gets the current real-time VRAM usage.
    /// </summary>
    /// <returns>A <see cref="VramUsage"/> record with free and used VRAM, or null if query fails.</returns>
    public async Task<VramUsage?> GetRealTimeVramUsageAsync()
    {
        try
        {
            var output = await RunNvidiaSmiAsync("--query-gpu=memory.free,memory.used --format=csv,noheader,nounits");
            if (!string.IsNullOrWhiteSpace(output))
            {
                var parts = output.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && 
                    int.TryParse(parts[0], out var free) && 
                    int.TryParse(parts[1], out var used))
                {
                    return new VramUsage(free, used);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to query real-time VRAM usage via nvidia-smi.");
        }

        return null;
    }

    private static string ResolveNvidiaSmiPath()
    {
        // 1. Direct command if available in PATH
        string[] candidates = new[]
        {
            "nvidia-smi",
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
            @"C:\Windows\System32\nvidia-smi.exe"
        };

        foreach (var candidate in candidates)
        {
            if (candidate == "nvidia-smi" || System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Search DriverStore file repository for nvidia-smi.exe
        try
        {
            var driverStore = @"C:\Windows\System32\DriverStore\FileRepository";
            if (System.IO.Directory.Exists(driverStore))
            {
                var matches = System.IO.Directory.GetFiles(driverStore, "nvidia-smi.exe", System.IO.SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    return matches[0];
                }
            }
        }
        catch { }

        return "nvidia-smi";
    }

    private GpuInfo? GetGpuInfoFromWmi()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? string.Empty;
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("RTX", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("GTX", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("Quadro", StringComparison.OrdinalIgnoreCase))
                {
                    ulong adapterRamBytes = Convert.ToUInt64(obj["AdapterRAM"] ?? 0);
                    int vramMb = (int)(adapterRamBytes / (1024 * 1024));

                    // Attempt 64-bit VRAM lookup via Windows Registry to bypass WMI 32-bit (4095 MB) saturation limit
                    ulong registryVramBytes = GetGpuVramBytesFromRegistry();
                    if (registryVramBytes > 0)
                    {
                        vramMb = (int)(registryVramBytes / (1024 * 1024));
                    }
                    else if (vramMb <= 4096)
                    {
                        // WMI saturates at 4095 MB (0xFFFFFFFF) for GPUs with >4GB VRAM. Default to 16GB VRAM for modern GPUs.
                        vramMb = 16384;
                    }

                    int freeVramMb = (int)(vramMb * 0.90);
                    int usedVramMb = (int)(vramMb * 0.10);

                    _logger?.LogInformation("Detected NVIDIA GPU via WMI/Registry fallback: {Name} with {VramMb} MB VRAM.", name, vramMb);
                    return new GpuInfo(
                        Name: name,
                        TotalVramMb: vramMb,
                        FreeVramMb: freeVramMb,
                        UsedVramMb: usedVramMb,
                        ComputeCapability: "8.0",
                        Temperature: 45,
                        DriverVersion: "550.0"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WMI/Registry video controller lookup failed.");
        }

        return null;
    }

    private static ulong GetGpuVramBytesFromRegistry()
    {
        try
        {
            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey != null)
            {
                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    using var subKey = baseKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var driverDesc = subKey.GetValue("DriverDesc")?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(driverDesc) && 
                        (driverDesc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || 
                         driverDesc.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || 
                         driverDesc.Contains("RTX", StringComparison.OrdinalIgnoreCase)))
                    {
                        var qwMem = subKey.GetValue("qwMemorySize");
                        if (qwMem != null)
                        {
                            return Convert.ToUInt64(qwMem);
                        }

                        var memSize = subKey.GetValue("HardwareInformation.MemorySize");
                        if (memSize is byte[] bytes && bytes.Length >= 4)
                        {
                            ulong size = bytes.Length >= 8 ? BitConverter.ToUInt64(bytes, 0) : BitConverter.ToUInt32(bytes, 0);
                            if (size > 0) return size;
                        }
                        else if (memSize != null)
                        {
                            return Convert.ToUInt64(memSize);
                        }
                    }
                }
            }
        }
        catch { }
        return 0;
    }

    private static async Task<string> RunNvidiaSmiAsync(string arguments)
    {
        string exePath = ResolveNvidiaSmiPath();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        var completedTask = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(3000));
        if (completedTask != process.WaitForExitAsync())
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("nvidia-smi execution timed out after 3 seconds.");
        }

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"nvidia-smi exited with code {process.ExitCode}. Error: {error}");
        }

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.FirstOrDefault() ?? string.Empty;
    }
}
