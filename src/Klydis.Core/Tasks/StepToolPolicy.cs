using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// Resolves the current step's allowed-tool set from the plan entry text. P1.7a: the Action
/// Gate enforces whatever this policy returns, and the prompt surfaces the same set — the
/// model can never call a tool the step forbids, even if it names it.
///
/// TEMPORARY BRIDGE: this is the same phrase-matching stopgap as the autonomous-correction
/// directive. The durable home is the first-class TaskStep record (ExpectedActionKind +
/// AllowedTools), which replaces text classification entirely. Until then the policy is
/// deliberately CONSERVATIVE: only explicit verification/summarization markers restrict the
/// set; everything else stays existence-gated so the model keeps its full legitimate surface.
///
/// Harness-control tools (plan, task_complete, task_progress, queue tools) are ALWAYS
/// allowed regardless of the step — step scoping governs workspace tools, never the control
/// surface that advances the plan or ends the run.
/// </summary>
public static class StepToolPolicy
{
    /// <summary>Tools that are never restricted by step scoping.</summary>
    public static readonly IReadOnlySet<string> ControlTools =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "plan", "task_complete", "task_progress",
            "check_message_queue", "incorporate_queued_message"
        };

    private static readonly string[] VerificationMarkers =
    {
        "verify the result", "verify the implementation", "verify the change",
        "run the build", "run the build and tests", "run the tests", "run a local preview",
        "final verification", "verify the deliverable", "verify the fix", "verify the output"
    };

    private static readonly string[] SummaryMarkers =
    {
        "summarize", "summarise", "present the deliverable", "write the final summary"
    };

    private static readonly string[] InspectionMarkers =
    {
        "inspect the", "explore the existing", "review the existing", "analyze the existing",
        "understand the existing", "examine the existing", "inspect the existing"
    };

    /// <summary>
    /// The allowed tool set for a step, or NULL when no restriction applies (the full
    /// registered surface, existence-gated). The returned set ALWAYS includes the control
    /// tools.
    /// </summary>
    public static IReadOnlySet<string>? ResolveAllowedTools(string? stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText)) return null;
        string t = stepText.ToLowerInvariant();

        HashSet<string>? allowed = null;
        if (VerificationMarkers.Any(t.Contains))
        {
            allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "run_command", "read_file", "list_directory", "search_files"
            };
        }
        else if (SummaryMarkers.Any(t.Contains))
        {
            allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "write_file", "read_file"
            };
        }
        else if (InspectionMarkers.Any(t.Contains))
        {
            allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "list_directory", "read_file", "search_files"
            };
        }

        if (allowed == null) return null;
        allowed.UnionWith(ControlTools);
        return allowed;
    }
}
