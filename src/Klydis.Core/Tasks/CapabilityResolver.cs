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
        ["filesystem.read"] = new[] { "read_file", "list_dir", "file_exists" },
        ["filesystem.write"] = new[] { "write_file", "str_replace", "create_file" },
        ["filesystem.edit"] = new[] { "str_replace", "write_file" },
        ["filesystem.list"] = new[] { "list_dir" },
        ["filesystem.search"] = new[] { "search_web", "list_dir" },
        ["shell.powershell"] = new[] { "run_command" },
        ["shell.cmd"] = new[] { "run_command" },
        ["process.start"] = new[] { "run_command" },
        ["process.wait"] = new[] { "run_command" },
        ["browser.crawl"] = new[] { "read_web_page", "search_web" },
        ["browser.search"] = new[] { "search_web" },
        ["git.status"] = new[] { "run_command" },
        ["git.diff"] = new[] { "run_command" },
        ["git.log"] = new[] { "run_command" }
    };

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
                allowedTools.Add("str_replace");
                allowedTools.Add("list_dir");
            }
            else if (capId.StartsWith("shell.", StringComparison.OrdinalIgnoreCase) ||
                     capId.StartsWith("process.", StringComparison.OrdinalIgnoreCase))
            {
                allowedTools.Add("run_command");
            }
            else if (capId.StartsWith("hardware.", StringComparison.OrdinalIgnoreCase) ||
                     capId.StartsWith("os.", StringComparison.OrdinalIgnoreCase) ||
                     capId.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
            {
                allowedTools.Add("run_command");
            }
        }

        return new CapabilityResolutionResult(capabilities, allowedTools, missing);
    }
}
