using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
    string DriverVersion);

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

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuProfiler"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public GpuProfiler(ILogger<GpuProfiler>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Queries the system for NVIDIA GPU information.
    /// </summary>
    /// <returns>A <see cref="GpuInfo"/> record if an NVIDIA GPU is found; otherwise, null.</returns>
    public async Task<GpuInfo?> GetGpuInfoAsync()
    {
        try
        {
            var output = await RunNvidiaSmiAsync("--query-gpu=name,memory.total,memory.free,memory.used,compute_cap,temperature.gpu,driver_version --format=csv,noheader,nounits");
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var parts = output.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 7)
            {
                return new GpuInfo(
                    Name: parts[0],
                    TotalVramMb: int.TryParse(parts[1], out var total) ? total : 0,
                    FreeVramMb: int.TryParse(parts[2], out var free) ? free : 0,
                    UsedVramMb: int.TryParse(parts[3], out var used) ? used : 0,
                    ComputeCapability: parts[4],
                    Temperature: int.TryParse(parts[5], out var temp) ? temp : 0,
                    DriverVersion: parts[6]
                );
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to query GPU info via nvidia-smi. The system might not have an NVIDIA GPU or drivers are missing.");
        }

        return null;
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
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var parts = output.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && 
                int.TryParse(parts[0], out var free) && 
                int.TryParse(parts[1], out var used))
            {
                return new VramUsage(free, used);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to query real-time VRAM usage via nvidia-smi.");
        }

        return null;
    }

    private static async Task<string> RunNvidiaSmiAsync(string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"nvidia-smi exited with code {process.ExitCode}. Error: {error}");
        }

        // Return the first line if multiple GPUs exist (simplified to primary GPU)
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.FirstOrDefault() ?? string.Empty;
    }
}
