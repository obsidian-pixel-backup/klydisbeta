using System;
using System.Collections.Generic;

namespace Klydis.Core.Tasks;

/// <summary>
/// How much side effect a tool call has, used by recovery/retry policy (review: never
/// blindly re-execute an action whose side effects you cannot undo). ReadOnly tools may be
/// re-attempted automatically after a failure; Destructive and ExternalSideEffect tools must
/// never be re-executed without explicit supervision — a timed-out delete_file or an
/// ambiguous run_command is NOT safe to re-run just because the harness wants progress.
/// </summary>
public enum ToolSideEffectLevel
{
    /// <summary>No state change: safe to re-run after failure (reads, listings, queries).</summary>
    ReadOnly,

    /// <summary>Safe to re-run because the operation is naturally idempotent (writes are
    /// content-replacements; re-running with the same content converges to the same state).</summary>
    Idempotent,

    /// <summary>Has real external effects (processes, network, persistence side effects);
    /// re-running may double the effect. Re-run only with explicit justification.</summary>
    ExternalSideEffect,

    /// <summary>Destructive: cannot be undone (deletes). NEVER auto-retried.</summary>
    Destructive
}

/// <summary>
/// The single deterministic owner of tool side-effect classification. Unknown tools classify
/// as <see cref="ToolSideEffectLevel.ExternalSideEffect"/> (conservative: never auto-retry a
/// tool whose effects are not understood).
/// </summary>
public static class ToolSideEffectClassifier
{
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "view_file", "list_directory", "list_dir", "search_files", "grep_search",
        "get_system_info", "system_report", "system_cpu_metrics", "system_gpu_metrics", "system_memory_metrics",
        "system_disk_metrics", "system_os_info", "system_processes",
        "search_web", "crawl_url", "check_message_queue", "retrieve_memory",
        "list_skills", "search_skills", "get_skill_details", "search_rag", "list_rag_collections",
        "recall_lessons", "list_custom_tools", "get_custom_tool"
    };

    private static readonly HashSet<string> IdempotentTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "edit_file", "replace_lines", "apply_patch", "structural_replace",
        "str_replace", "store_memory", "index_folder_rag",
        "activate_skill", "learn_skill", "learn_lesson", "task_progress", "plan",
        "incorporate_queued_message", "summarize_context"
    };

    private static readonly HashSet<string> DestructiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete_skill", "delete_custom_tool", "delete_file", "remove_file", "clear_rag"
    };

    /// <summary>Classifies a tool by its registered name and optional arguments. Conservative for unknown names.</summary>
    public static ToolSideEffectLevel Classify(string? toolName, IDictionary<string, object>? args = null)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return ToolSideEffectLevel.ExternalSideEffect;

        if (string.Equals(toolName, "manage_process", StringComparison.OrdinalIgnoreCase))
        {
            if (args != null && (args.TryGetValue("action", out var actionObj) || args.TryGetValue("act", out actionObj)))
            {
                string? action = actionObj?.ToString();
                if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolSideEffectLevel.ReadOnly;
                }
                if (string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolSideEffectLevel.Destructive;
                }
            }
            return ToolSideEffectLevel.ExternalSideEffect;
        }

        if (ReadOnlyTools.Contains(toolName)) return ToolSideEffectLevel.ReadOnly;
        if (IdempotentTools.Contains(toolName)) return ToolSideEffectLevel.Idempotent;
        if (DestructiveTools.Contains(toolName)) return ToolSideEffectLevel.Destructive;
        return ToolSideEffectLevel.ExternalSideEffect;
    }

    /// <summary>
    /// True when a failed/ambiguous execution of this tool may be re-attempted automatically:
    /// only pure reads qualify. Writes (even idempotent ones) and commands need a reasoned
    /// retry, never an automatic one.
    /// </summary>
    public static bool IsSafeToAutoRetry(string? toolName)
        => Classify(toolName) == ToolSideEffectLevel.ReadOnly;
}
