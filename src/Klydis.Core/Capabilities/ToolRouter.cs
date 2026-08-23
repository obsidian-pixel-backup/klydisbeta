using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Capabilities;

/// <summary>
/// A candidate tool for a capability with priority score.
/// </summary>
public sealed record ToolCandidate(
    string ToolName,
    string Capability,
    int Score,
    bool IsFallback = false);

/// <summary>
/// Routes and ranks tools for required capabilities.
/// Enforces runtime policy where specialized native tools are overwhelmingly
/// preferred over generic shell execution (score 100 vs 20).
/// </summary>
public static class ToolRouter
{
    private static readonly Dictionary<string, List<(string ToolName, int Score)>> CapabilityToolMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [CapabilityIdentifiers.CpuTelemetry] = new()
            {
                ("system_cpu_info", 100),
                ("system_cpu_usage", 95),
                ("system_cpu_metrics", 90),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.GpuTelemetry] = new()
            {
                ("system_gpu_info", 100),
                ("system_gpu_usage", 95),
                ("system_gpu_metrics", 90),
                ("system_gpu_processes", 85),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.MemoryTelemetry] = new()
            {
                ("system_memory", 100),
                ("system_memory_metrics", 95),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.DiskTelemetry] = new()
            {
                ("system_disks", 100),
                ("system_disk_metrics", 95),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.ThermalTelemetry] = new()
            {
                ("system_temperatures", 100),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.OsInfo] = new()
            {
                ("system_os", 100),
                ("system_os_info", 95),
                ("system_report", 90),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.OsUptime] = new()
            {
                ("system_uptime", 100),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.ProcessInspection] = new()
            {
                ("system_top_processes", 100),
                ("system_processes", 95),
                ("process_find", 90),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.SystemDiagnostics] = new()
            {
                ("system_report", 100),
                ("system_hardware_report", 95),
                ("system_software_report", 95),
                ("system_cpu_info", 90),
                ("system_gpu_info", 90),
                ("system_memory", 90),
                ("system_disks", 90),
                ("system_processes", 85),
                ("run_command", 20)
            },
            [CapabilityIdentifiers.FileRead] = new()
            {
                ("read_file", 100),
                ("list_directory", 80),
                ("search_files", 80)
            },
            [CapabilityIdentifiers.FileWrite] = new()
            {
                ("write_file", 100),
                ("edit_file", 95),
                ("replace_lines", 90),
                ("apply_patch", 85)
            },
            [CapabilityIdentifiers.FileEdit] = new()
            {
                ("edit_file", 100),
                ("replace_lines", 95),
                ("apply_patch", 90),
                ("write_file", 85)
            },
            [CapabilityIdentifiers.FileList] = new()
            {
                ("list_directory", 100),
                ("search_files", 80)
            },
            [CapabilityIdentifiers.FileSearch] = new()
            {
                ("search_files", 100),
                ("list_directory", 85)
            },
            [CapabilityIdentifiers.CodeInspection] = new()
            {
                ("read_file", 100),
                ("search_files", 95),
                ("list_directory", 85)
            },
            [CapabilityIdentifiers.BuildVerify] = new()
            {
                ("run_command", 100)
            },
            [CapabilityIdentifiers.TestVerify] = new()
            {
                ("run_command", 100)
            },
            [CapabilityIdentifiers.WebSearch] = new()
            {
                ("search_web", 100),
                ("crawl_url", 85)
            },
            [CapabilityIdentifiers.WebCrawl] = new()
            {
                ("crawl_url", 100),
                ("search_web", 80)
            },
            [CapabilityIdentifiers.ShellExecution] = new()
            {
                ("run_command", 100)
            }
        };

    /// <summary>
    /// Returns ranked candidates for a specific capability.
    /// </summary>
    public static IReadOnlyList<ToolCandidate> ResolveRankedTools(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability)) return Array.Empty<ToolCandidate>();

        if (CapabilityToolMap.TryGetValue(capability, out var tools))
        {
            return tools.Select(t => new ToolCandidate(t.ToolName, capability, t.Score, t.Score <= 20)).ToList();
        }

        // Prefix match fallback
        if (capability.StartsWith("hardware.", StringComparison.OrdinalIgnoreCase) ||
            capability.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
        {
            return new List<ToolCandidate>
            {
                new("system_report", capability, 90),
                new("run_command", capability, 20, IsFallback: true)
            };
        }

        if (capability.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase))
        {
            return new List<ToolCandidate>
            {
                new("read_file", capability, 90),
                new("list_directory", capability, 85)
            };
        }

        return new List<ToolCandidate> { new("run_command", capability, 20, IsFallback: true) };
    }

    /// <summary>
    /// Gets the single highest-ranking preferred tool for a capability.
    /// </summary>
    public static string GetPreferredTool(string capability)
    {
        var ranked = ResolveRankedTools(capability);
        return ranked.Count > 0 ? ranked[0].ToolName : "run_command";
    }

    /// <summary>
    /// Resolves and deduplicates all allowed tools for a set of capabilities ordered by rank.
    /// </summary>
    public static IReadOnlyList<string> GetRankedAllowedToolNames(IEnumerable<string> capabilities)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allCandidates = new List<ToolCandidate>();
        foreach (var cap in capabilities)
        {
            allCandidates.AddRange(ResolveRankedTools(cap));
        }

        foreach (var cand in allCandidates.OrderByDescending(c => c.Score))
        {
            if (seen.Add(cand.ToolName))
            {
                result.Add(cand.ToolName);
            }
        }

        return result;
    }
}
