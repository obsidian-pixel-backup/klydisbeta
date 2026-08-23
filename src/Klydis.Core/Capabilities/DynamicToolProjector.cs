using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;
using Klydis.Core.Tasks;

namespace Klydis.Core.Capabilities;

/// <summary>
/// Dynamic Tool Projector (P1) that filters the large registered tool surface (80+ tools)
/// down to a focused set of 3–8 relevant tools based on the active step and required capability.
/// Prevents tool-surface overload and hallucinated tool calls across small and large models.
/// </summary>
public static class DynamicToolProjector
{
    private static readonly string[] EssentialControlTools =
    {
        "plan", "task_complete", "task_progress"
    };

    private static readonly string[] DiagnosticTools =
    {
        "system_cpu_usage", "system_cpu_info", "system_memory_metrics", "system_memory",
        "system_gpu_metrics", "system_gpu_info", "system_os_info", "system_processes",
        "system_disks", "system_report", "system_uptime"
    };

    private static readonly string[] FileMutationTools =
    {
        "read_file", "write_file", "edit_file", "search_files", "list_directory"
    };

    private static readonly string[] WebTools =
    {
        "search_web", "crawl_url", "get_links", "get_section"
    };

    private static readonly string[] TerminalTools =
    {
        "run_command", "read_file", "search_files"
    };

    /// <summary>
    /// Projects a focused subset of tools relevant to the current task step and model policy.
    /// Essential control tools (plan, task_complete) are always guaranteed.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> ProjectTools(
        IEnumerable<ToolDefinition> registeredTools,
        TaskStep? currentStep,
        int maxToolCount = 8)
    {
        if (registeredTools == null) return Array.Empty<ToolDefinition>();

        var allTools = registeredTools.ToList();
        if (allTools.Count <= maxToolCount) return allTools;

        var selected = new List<ToolDefinition>();
        var toolMap = allTools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        // 1. ALWAYS add essential control tools first so they are never truncated
        foreach (var ctrl in EssentialControlTools)
        {
            if (toolMap.TryGetValue(ctrl, out var ctrlDef) && !selected.Contains(ctrlDef))
            {
                selected.Add(ctrlDef);
            }
        }

        // 2. If the step explicitly defines AllowedTools, prioritize those
        if (currentStep?.AllowedTools != null && currentStep.AllowedTools.Count > 0)
        {
            foreach (var name in currentStep.AllowedTools)
            {
                if (toolMap.TryGetValue(name, out var toolDef) && !selected.Contains(toolDef))
                {
                    selected.Add(toolDef);
                    if (selected.Count >= maxToolCount) break;
                }
            }
        }
        else if (currentStep != null)
        {
            // 3. Project based on ExpectedActionKind
            var candidateNames = currentStep.ExpectedActionKind switch
            {
                StepActionKind.Inspect or StepActionKind.FileMutation => FileMutationTools,
                StepActionKind.Research => WebTools,
                StepActionKind.CommandExecution or StepActionKind.TerminalInteraction => TerminalTools,
                StepActionKind.Verification => TerminalTools,
                _ => DiagnosticTools
            };

            foreach (var name in candidateNames)
            {
                if (toolMap.TryGetValue(name, out var toolDef) && !selected.Contains(toolDef))
                {
                    selected.Add(toolDef);
                    if (selected.Count >= maxToolCount) break;
                }
            }
        }

        // 4. Fill up to maxToolCount with remaining registered tools if space remains
        if (selected.Count < maxToolCount)
        {
            foreach (var tool in allTools)
            {
                if (!selected.Contains(tool))
                {
                    selected.Add(tool);
                    if (selected.Count >= maxToolCount) break;
                }
            }
        }

        return selected.Take(maxToolCount).ToList();
    }
}
