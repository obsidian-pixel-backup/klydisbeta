using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;

namespace Klydis.Core.Capabilities.Providers;

/// <summary>
/// Capability: system.processes.top / system_top_processes
/// Returns the top N processes by CPU or RAM utilization deterministically.
/// Eliminates the need for models to invent pipeline / search_text tools.
/// </summary>
public sealed class SystemTopProcessesCapability : ICapability
{
    public string Id => "system.processes.top";
    public CapabilityDomain Domain => CapabilityDomain.Process;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Returns top running processes sorted deterministically by CPU usage or memory consumption.",
        Parameters: new[]
        {
            new CapabilityParameter("sort_by", "string", "Metric to sort by: 'cpu' or 'memory' (default: 'cpu').", Required: false, EnumValues: new[] { "cpu", "memory", "ram" }),
            new CapabilityParameter("limit", "integer", "Maximum number of processes to return (default: 5, max: 20).", Required: false)
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
            string sortBy = "cpu";
            if (request.Parameters.TryGetValue("sort_by", out var sortObj) && sortObj != null)
            {
                string s = sortObj.ToString()?.ToLowerInvariant() ?? "cpu";
                if (s.Contains("mem") || s.Contains("ram")) sortBy = "memory";
            }

            int limit = 5;
            if (request.Parameters.TryGetValue("limit", out var limitObj) && limitObj != null)
            {
                if (int.TryParse(limitObj.ToString(), out int parsedLimit) && parsedLimit > 0)
                {
                    limit = Math.Min(parsedLimit, 20);
                }
            }

            var procs = Process.GetProcesses();
            var list = new List<object>();

            if (sortBy == "memory")
            {
                var sorted = procs.OrderByDescending(p =>
                {
                    try { return p.WorkingSet64; } catch { return 0; }
                }).Take(limit);

                foreach (var p in sorted)
                {
                    try
                    {
                        double memMb = Math.Round((double)p.WorkingSet64 / (1024 * 1024), 2);
                        list.Add(new
                        {
                            Pid = p.Id,
                            Name = p.ProcessName,
                            MemoryMb = memMb
                        });
                    }
                    catch { }
                }
            }
            else
            {
                // Sort by WorkingSet as safe proxy or process list
                var sorted = procs.OrderByDescending(p =>
                {
                    try { return p.TotalProcessorTime.TotalMilliseconds; } catch { return 0; }
                }).Take(limit);

                foreach (var p in sorted)
                {
                    try
                    {
                        double memMb = Math.Round((double)p.WorkingSet64 / (1024 * 1024), 2);
                        list.Add(new
                        {
                            Pid = p.Id,
                            Name = p.ProcessName,
                            MemoryMb = memMb,
                            TotalCpuTimeSec = Math.Round(p.TotalProcessorTime.TotalSeconds, 1)
                        });
                    }
                    catch { }
                }
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, list, sw.Elapsed, evidence));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success || result.Data is null)
            return Task.FromResult(VerificationResult.Failed("Failed to inspect top processes."));

        var facts = new List<FactAssertion>
        {
            new("process", "top", "list", result.Data, TimeSpan.FromSeconds(30), Id)
        };

        return Task.FromResult(VerificationResult.Verified("Top processes verified.", facts));
    }
}

/// <summary>
/// Capability: system.processes.find / process_find
/// Finds running processes matching a name or query filter.
/// </summary>
public sealed class ProcessFindCapability : ICapability
{
    public string Id => "system.processes.find";
    public CapabilityDomain Domain => CapabilityDomain.Process;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Searches running processes matching a name substring or query.",
        Parameters: new[]
        {
            new CapabilityParameter("name", "string", "Process name or substring to search for (e.g. 'chrome', 'ollama', 'klydis').", Required: true)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!request.Parameters.ContainsKey("name") || string.IsNullOrWhiteSpace(request.Parameters["name"]?.ToString()))
        {
            return Task.FromResult(PreconditionCheckResult.Failed("Missing required parameter: 'name'."));
        }
        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string query = request.Parameters["name"]?.ToString()?.Trim().ToLowerInvariant() ?? "";
            var matches = Process.GetProcesses()
                .Where(p =>
                {
                    try { return p.ProcessName.ToLowerInvariant().Contains(query); }
                    catch { return false; }
                })
                .Select(p =>
                {
                    try
                    {
                        return new
                        {
                            Pid = p.Id,
                            Name = p.ProcessName,
                            MemoryMb = Math.Round((double)p.WorkingSet64 / (1024 * 1024), 2)
                        };
                    }
                    catch
                    {
                        return new { Pid = p.Id, Name = p.ProcessName, MemoryMb = 0.0 };
                    }
                })
                .Take(25)
                .ToList();

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(matches, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, matches, sw.Elapsed, evidence));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success || result.Data is null)
            return Task.FromResult(VerificationResult.Failed("Failed to search processes."));

        var facts = new List<FactAssertion>
        {
            new("process", "find", "matches", result.Data, TimeSpan.FromSeconds(30), Id)
        };

        return Task.FromResult(VerificationResult.Verified("Process matches verified.", facts));
    }
}
