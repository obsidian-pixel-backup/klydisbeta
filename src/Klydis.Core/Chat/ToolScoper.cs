using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Chat;

/// <summary>
/// Dynamically scopes and prunes the exposed tool definition set per execution step
/// to prevent context saturation and decision paralysis for 12B models.
/// </summary>
public static class ToolScoper
{
    private static readonly string[] AlwaysCoreTools =
    {
        "plan",
        "task_complete",
        "task_progress"
    };

    private static readonly string[] StandardFileOps =
    {
        "read_file",
        "write_file",
        "edit_file",
        "list_directory",
        "run_command"
    };

    /// <summary>
    /// Filters and scopes the full list of available tools to a compact, context-relevant subset.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> ScopeTools(
        IReadOnlyList<ToolDefinition> allTools,
        IEnumerable<string>? relevantCapabilities = null,
        IEnumerable<string>? preferredToolNames = null,
        int maxTools = 12)
    {
        if (allTools == null || allTools.Count == 0)
        {
            return Array.Empty<ToolDefinition>();
        }

        var relevantCapSet = new HashSet<string>(relevantCapabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var preferredSet = new HashSet<string>(preferredToolNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var scoped = new List<ToolDefinition>();
        var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(ToolDefinition def)
        {
            if (scoped.Count < maxTools && addedNames.Add(def.Name))
            {
                scoped.Add(def);
            }
        }

        // 1. Explicitly preferred / matched tools from direct intent / active capabilities
        foreach (var tool in allTools)
        {
            if (preferredSet.Contains(tool.Name) ||
                relevantCapSet.Contains(tool.Name) ||
                relevantCapSet.Any(cap => tool.Name.Contains(cap, StringComparison.OrdinalIgnoreCase)))
            {
                TryAdd(tool);
            }
        }

        // 2. Add Core orchestration tools
        foreach (var coreName in AlwaysCoreTools)
        {
            var coreTool = allTools.FirstOrDefault(t => string.Equals(t.Name, coreName, StringComparison.OrdinalIgnoreCase));
            if (coreTool != null)
            {
                TryAdd(coreTool);
            }
        }

        // 3. If space remains, add standard execution / filesystem tools
        foreach (var stdName in StandardFileOps)
        {
            if (scoped.Count >= maxTools) break;
            var stdTool = allTools.FirstOrDefault(t => string.Equals(t.Name, stdName, StringComparison.OrdinalIgnoreCase));
            if (stdTool != null)
            {
                TryAdd(stdTool);
            }
        }

        // 4. If still under minimum, backfill from remaining tools
        if (scoped.Count < 5)
        {
            foreach (var tool in allTools)
            {
                if (scoped.Count >= maxTools) break;
                TryAdd(tool);
            }
        }

        return scoped;
    }
}
