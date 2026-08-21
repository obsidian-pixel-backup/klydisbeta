using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;
using Klydis.Core.Hardware;

namespace Klydis.Core.Capabilities.Providers;

/// <summary>
/// Capability: ai.gpu.inspect
/// Inspects GPU VRAM allocation, CUDA compute capability, and inference load.
/// </summary>
public sealed class AiGpuInspectCapability : ICapability
{
    private readonly GpuProfiler? _gpuProfiler;

    public AiGpuInspectCapability(GpuProfiler? gpuProfiler = null)
    {
        _gpuProfiler = gpuProfiler;
    }

    public string Id => "ai.gpu.inspect";
    public CapabilityDomain Domain => CapabilityDomain.LocalAI;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Inspects GPU VRAM allocation, CUDA compute architecture, and real-time inference headroom.",
        Parameters: Array.Empty<CapabilityParameter>(),
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var info = _gpuProfiler != null ? await _gpuProfiler.GetGpuInfoAsync() : null;
            object data;

            if (info != null)
            {
                data = new
                {
                    GpuName = info.Name,
                    TotalVramMb = info.TotalVramMb,
                    FreeVramMb = info.FreeVramMb,
                    UsedVramMb = info.UsedVramMb,
                    UtilizationPercent = info.GpuUtilPercent,
                    ComputeCapability = info.ComputeCapability,
                    DriverVersion = info.DriverVersion,
                    TemperatureC = info.Temperature,
                    SupportsCuda = true
                };
            }
            else
            {
                data = new
                {
                    GpuName = "CPU / Fallback Backend",
                    TotalVramMb = 0,
                    FreeVramMb = 0,
                    UsedVramMb = 0,
                    UtilizationPercent = 0,
                    ComputeCapability = "N/A",
                    DriverVersion = "N/A",
                    TemperatureC = 0,
                    SupportsCuda = false
                };
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success || result.Data is null) return Task.FromResult(VerificationResult.Failed("AI GPU inspect failed."));
        var facts = new List<FactAssertion>
        {
            new("ai", "gpu", "telemetry", result.Data, TimeSpan.FromSeconds(2), Id)
        };
        return Task.FromResult(VerificationResult.Verified("AI GPU metrics verified.", facts));
    }
}

/// <summary>
/// Capability: ai.models.list
/// Discovers and lists local GGUF models in cache and user directories.
/// </summary>
public sealed class AiModelsListCapability : ICapability
{
    public string Id => "ai.models.list";
    public CapabilityDomain Domain => CapabilityDomain.LocalAI;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Lists local GGUF models available in Klydis cache, user profiles, and working directories with sizes and quantizations.",
        Parameters: new List<CapabilityParameter>
        {
            new("search_directory", "string", "Optional directory path to scan for models.", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? searchDir = request.GetParam<string>("search_directory");
            var searchPaths = new List<string>();

            if (!string.IsNullOrEmpty(searchDir) && Directory.Exists(searchDir))
            {
                searchPaths.Add(searchDir);
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            searchPaths.Add(Path.Combine(userProfile, ".klydis", "models"));
            searchPaths.Add(Path.Combine(userProfile, ".ollama", "models"));
            searchPaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "models"));

            var models = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in searchPaths)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories))
                    {
                        if (seen.Add(file))
                        {
                            var fi = new FileInfo(file);
                            models.Add(new
                            {
                                Name = Path.GetFileNameWithoutExtension(file),
                                FileName = Path.GetFileName(file),
                                Path = file,
                                SizeMb = Math.Round((double)fi.Length / (1024 * 1024), 2),
                                SizeGb = Math.Round((double)fi.Length / (1024 * 1024 * 1024), 2),
                                LastModifiedUtc = fi.LastWriteTimeUtc
                            });
                        }
                    }
                }
                catch { /* best effort directory scan */ }
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow,
                StructuredMetrics: new Dictionary<string, object?> { ["TotalModels"] = models.Count }
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, models, sw.Elapsed, evidence));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Model listing failed."));
        return Task.FromResult(VerificationResult.Verified("Local AI models enumerated."));
    }
}
