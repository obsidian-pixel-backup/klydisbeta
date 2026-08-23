using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Capabilities;
using Klydis.Core.Capabilities.Bridge;

namespace Klydis.Core.Tasks;

/// <summary>
/// Resolution result mapping a task's declared capabilities to runtime tools and capabilities.
/// </summary>
public sealed record CapabilityResolutionResult(
    IReadOnlyList<ICapability> Capabilities,
    IReadOnlySet<string> AllowedToolNames,
    IReadOnlyList<string> MissingCapabilities);

/// <summary>
/// Resolves required capabilities on <see cref="PlanTask"/> instances to registered capabilities,
/// skill activations, and allowed runtime tool surfaces.
/// </summary>
public static class CapabilityResolver
{
    private static readonly Dictionary<string, string[]> CapabilityToTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["filesystem.read"] = new[] { "read_file", "list_directory", "search_files", "file_exists" },
        ["filesystem.write"] = new[] { "write_file", "edit_file", "replace_lines", "apply_patch" },
        ["filesystem.edit"] = new[] { "edit_file", "replace_lines", "write_file" },
        ["filesystem.list"] = new[] { "list_directory" },
        ["filesystem.search"] = new[] { "search_files", "list_directory" },
        ["hardware.cpu"] = new[] { "system_cpu_info", "system_cpu_usage", "system_cpu_metrics", "run_command" },
        ["hardware.gpu"] = new[] { "system_gpu_info", "system_gpu_usage", "system_gpu_metrics", "run_command" },
        ["hardware.memory"] = new[] { "system_memory", "system_memory_metrics", "run_command" },
        ["hardware.ram"] = new[] { "system_memory", "system_memory_metrics", "run_command" },
        ["hardware.disk"] = new[] { "system_disks", "system_disk_metrics", "run_command" },
        ["hardware.thermal"] = new[] { "system_temperatures", "run_command" },
        ["os.info"] = new[] { "system_os_info", "system_os", "system_report", "run_command" },
        ["os.uptime"] = new[] { "system_uptime", "run_command" },
        ["process.inspection"] = new[] { "system_processes", "system_top_processes", "process_find", "run_command" },
        ["system.diagnostics"] = new[] { "system_report", "system_hardware_report", "system_software_report", "system_cpu_info", "system_gpu_info", "system_memory", "system_disks", "system_uptime", "system_processes", "run_command" },
        ["shell.powershell"] = new[] { "run_command" },
        ["shell.cmd"] = new[] { "run_command" },
        ["process.start"] = new[] { "run_command" },
        ["process.wait"] = new[] { "run_command" },
        ["browser.crawl"] = new[] { "read_web_page", "crawl_url", "search_web" },
        ["browser.search"] = new[] { "search_web" },
        ["git.status"] = new[] { "run_command" },
        ["git.diff"] = new[] { "run_command" },
        ["git.log"] = new[] { "run_command" }
    };

    /// <summary>
    /// Returns priority score for tool selection (higher = preferred specialized tool, lower = generic fallback).
    /// </summary>
    public static int GetToolPriority(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return 0;
        string lower = toolName.ToLowerInvariant();

        if (lower.StartsWith("system_") || lower.StartsWith("process_"))
            return 100; // Deterministic, read-only specialized platform tools

        if (lower is "read_file" or "search_files" or "list_directory" or "write_file" or "edit_file" or "search_web")
            return 80; // High-level workspace/web tools

        if (lower is "run_command")
            return 20; // Generic shell execution fallback

        return 50;
    }

    /// <summary>
    /// True when the tool is a specialized, read-only system diagnostic inspection tool.
    /// </summary>
    public static bool IsSpecializedDiagnosticTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        string lower = toolName.ToLowerInvariant();
        return lower.StartsWith("system_") || lower.StartsWith("process_") || lower is "get_system_info";
    }

    /// <summary>
    /// Resolves required capabilities into allowed tools and concrete capability instances.
    /// </summary>
    public static CapabilityResolutionResult Resolve(
        PlanTask task,
        ICapabilityRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var capabilities = new List<ICapability>();
        var allowedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        // Harness control tools are always allowed regardless of task
        allowedTools.Add("plan");
        allowedTools.Add("task_complete");
        allowedTools.Add("task_progress");
        allowedTools.Add("check_message_queue");
        allowedTools.Add("incorporate_queued_message");

        if (task.RequiredCapabilities == null || task.RequiredCapabilities.Count == 0)
        {
            // If no capabilities specified, allow all standard tools by default
            return new CapabilityResolutionResult(capabilities, allowedTools, missing);
        }

        foreach (var capId in task.RequiredCapabilities)
        {
            if (string.IsNullOrWhiteSpace(capId)) continue;

            if (registry != null)
            {
                var cap = registry.Get(capId);
                if (cap != null)
                {
                    capabilities.Add(cap);
                }
                else
                {
                    missing.Add(capId);
                }
            }

            if (CapabilityToTools.TryGetValue(capId, out var toolNames))
            {
                foreach (var tool in toolNames)
                {
                    allowedTools.Add(tool);
                }
            }
            else if (capId.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase))
            {
                allowedTools.Add("read_file");
                allowedTools.Add("write_file");
                allowedTools.Add("edit_file");
                allowedTools.Add("replace_lines");
                allowedTools.Add("list_directory");
                allowedTools.Add("search_files");
            }
            else if (capId.StartsWith("hardware.", StringComparison.OrdinalIgnoreCase) ||
                     capId.StartsWith("os.", StringComparison.OrdinalIgnoreCase) ||
                     capId.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
            {
                allowedTools.Add("system_cpu_info");
                allowedTools.Add("system_gpu_info");
                allowedTools.Add("system_memory");
                allowedTools.Add("system_disks");
                allowedTools.Add("system_os_info");
                allowedTools.Add("system_temperatures");
                allowedTools.Add("system_processes");
                allowedTools.Add("system_top_processes");
                allowedTools.Add("system_uptime");
                allowedTools.Add("system_report");
                allowedTools.Add("run_command");
            }
            else if (capId.StartsWith("shell.", StringComparison.OrdinalIgnoreCase) ||
                     capId.StartsWith("process.", StringComparison.OrdinalIgnoreCase))
            {
                allowedTools.Add("process_find");
                allowedTools.Add("system_processes");
                allowedTools.Add("run_command");
            }
        }

        return new CapabilityResolutionResult(capabilities, allowedTools, missing);
    }
}
