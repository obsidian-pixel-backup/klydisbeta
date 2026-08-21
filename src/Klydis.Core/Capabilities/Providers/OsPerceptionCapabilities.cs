using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;

#pragma warning disable CA1416

namespace Klydis.Core.Capabilities.Providers;

/// <summary>
/// Capability: os.system.info
/// Inspects OS details, build, architecture, and machine uptime.
/// </summary>
public sealed class SystemInfoCapability : ICapability
{
    public string Id => "os.system.info";
    public CapabilityDomain Domain => CapabilityDomain.OperatingSystem;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Returns operating system version, OS build, system architecture, computer name, logged-in user, and system uptime.",
        Parameters: Array.Empty<CapabilityParameter>(),
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var data = new
            {
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                OsDescription = RuntimeInformation.OSDescription,
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                FrameworkDescription = RuntimeInformation.FrameworkDescription,
                Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                ProcessorCount = Environment.ProcessorCount,
                UptimeHours = Math.Round(uptime.TotalHours, 2),
                SystemDirectory = Environment.SystemDirectory,
                CurrentDirectory = Environment.CurrentDirectory
            };

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence));
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
            return Task.FromResult(VerificationResult.Failed("Failed to inspect OS system info."));

        var facts = new List<FactAssertion>
        {
            new("os", "system", "info", result.Data, TimeSpan.FromHours(24), Id)
        };

        return Task.FromResult(VerificationResult.Verified("OS system info verified.", facts));
    }
}

/// <summary>
/// Capability: os.environment.get
/// Inspects environment variables or search paths.
/// </summary>
public sealed class EnvironmentGetCapability : ICapability
{
    public string Id => "os.environment.get";
    public CapabilityDomain Domain => CapabilityDomain.OperatingSystem;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Retrieves specific or all environment variables and PATH directory entries.",
        Parameters: new List<CapabilityParameter>
        {
            new("variable_name", "string", "Optional specific environment variable name (e.g. 'PATH', 'USERPROFILE', 'CUDA_PATH').", false)
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
            string? varName = request.GetParam<string>("variable_name");
            object data;

            if (!string.IsNullOrWhiteSpace(varName))
            {
                string? val = Environment.GetEnvironmentVariable(varName);
                data = new
                {
                    Variable = varName,
                    Value = val,
                    Exists = val is not null
                };
            }
            else
            {
                var dict = new Dictionary<string, string>();
                var env = Environment.GetEnvironmentVariables();
                foreach (var key in env.Keys)
                {
                    if (key is string k && env[key] is string v)
                    {
                        dict[k] = v;
                    }
                }
                data = dict;
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence));
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
            return Task.FromResult(VerificationResult.Failed("Failed to read environment."));

        return Task.FromResult(VerificationResult.Verified("Environment read verified."));
    }
}

/// <summary>
/// Capability: os.processes.enumerate
/// Enumerates running processes with PID, working set, and thread count.
/// </summary>
public sealed class ProcessEnumerateCapability : ICapability
{
    public string Id => "os.processes.enumerate";
    public CapabilityDomain Domain => CapabilityDomain.OperatingSystem;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Enumerates currently running OS processes, memory working set, and thread counts. Supports sorting and name filtering.",
        Parameters: new List<CapabilityParameter>
        {
            new("filter_name", "string", "Optional process name substring filter (case-insensitive).", false),
            new("sort_by", "string", "Sort ordering: 'memory' (default), 'name', or 'pid'.", false, new[] { "memory", "name", "pid" }),
            new("limit", "integer", "Maximum number of processes to return (default: 30, max: 100).", false)
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
            string? filter = request.GetParam<string>("filter_name");
            string sortBy = request.GetParam<string>("sort_by", "memory") ?? "memory";
            int limit = Math.Clamp(request.GetParam<int>("limit", 30), 1, 100);

            var processes = Process.GetProcesses();
            var list = new List<object>();

            foreach (var p in processes)
            {
                try
                {
                    string name = p.ProcessName;
                    if (!string.IsNullOrWhiteSpace(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long memMb = p.WorkingSet64 / (1024 * 1024);
                    list.Add(new
                    {
                        Pid = p.Id,
                        Name = name,
                        MemoryMb = memMb,
                        ThreadCount = p.Threads.Count,
                        Responding = p.Responding
                    });
                }
                catch
                {
                    // Process may have exited while enumerating
                }
                finally
                {
                    p.Dispose();
                }
            }

            IEnumerable<object> sorted = sortBy.ToLowerInvariant() switch
            {
                "name" => list.OrderBy(x => ((dynamic)x).Name),
                "pid" => list.OrderBy(x => ((dynamic)x).Pid),
                _ => list.OrderByDescending(x => ((dynamic)x).MemoryMb)
            };

            var finalResults = sorted.Take(limit).ToList();
            sw.Stop();

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(finalResults, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow,
                StructuredMetrics: new Dictionary<string, object?> { ["TotalProcesses"] = processes.Length }
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, finalResults, sw.Elapsed, evidence));
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
            return Task.FromResult(VerificationResult.Failed("Failed to enumerate processes."));

        var facts = new List<FactAssertion>
        {
            new("os", "processes", "process_list", result.Data, TimeSpan.FromSeconds(2), Id)
        };

        return Task.FromResult(VerificationResult.Verified("Process state verified.", facts));
    }
}

/// <summary>
/// Capability: os.services.enumerate
/// Enumerates Windows background services and their statuses.
/// </summary>
public sealed class ServicesEnumerateCapability : ICapability
{
    public string Id => "os.services.enumerate";
    public CapabilityDomain Domain => CapabilityDomain.OperatingSystem;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Enumerates Windows services, display names, and current running/stopped status.",
        Parameters: new List<CapabilityParameter>
        {
            new("filter_name", "string", "Optional service name or display name filter.", false),
            new("status", "string", "Filter by status: 'Running', 'Stopped', or 'All' (default).", false, new[] { "All", "Running", "Stopped" })
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
            string? filter = request.GetParam<string>("filter_name");
            string statusFilter = request.GetParam<string>("status", "All") ?? "All";

            var list = new List<object>();

            if (OperatingSystem.IsWindows())
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode FROM Win32_Service");
                foreach (var obj in searcher.Get())
                {
                    try
                    {
                        string name = obj["Name"]?.ToString() ?? "";
                        string displayName = obj["DisplayName"]?.ToString() ?? "";
                        string state = obj["State"]?.ToString() ?? "";
                        string startMode = obj["StartMode"]?.ToString() ?? "";

                        if (statusFilter.Equals("Running", StringComparison.OrdinalIgnoreCase) && !state.Equals("Running", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (statusFilter.Equals("Stopped", StringComparison.OrdinalIgnoreCase) && !state.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!string.IsNullOrWhiteSpace(filter))
                        {
                            if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                                !displayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        list.Add(new
                        {
                            ServiceName = name,
                            DisplayName = displayName,
                            Status = state,
                            StartMode = startMode
                        });

                        if (list.Count >= 50) break;
                    }
                    catch
                    {
                        // Service access denied or transient error
                    }
                }
            }

            var finalResults = list.Take(50).ToList();
            sw.Stop();

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(finalResults, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, finalResults, sw.Elapsed, evidence));
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
            return Task.FromResult(VerificationResult.Failed("Failed to enumerate services."));

        var facts = new List<FactAssertion>
        {
            new("os", "services", "services_summary", result.Data, TimeSpan.FromSeconds(30), Id)
        };

        return Task.FromResult(VerificationResult.Verified("Services state verified.", facts));
    }
}
