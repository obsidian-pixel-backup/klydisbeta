using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// The 4-tier failure hierarchy distinguishing generation errors from action, plan, and goal failures.
/// Prevents local model mistakes from prematurely killing autonomous goals.
/// </summary>
public enum FailureTier
{
    /// <summary>Model output invalid, unparseable, or hallucinated -> Turn-level repair.</summary>
    Generation,

    /// <summary>Tool failed, non-zero exit code, or access error -> Action-level retry / alternative tool.</summary>
    Tool,

    /// <summary>Work item blocked or dependencies unmet -> Plan-level replanning.</summary>
    WorkItem,

    /// <summary>Hard budget exhausted or unrecoverable error -> Terminal goal failure.</summary>
    Goal
}

/// <summary>
/// Encapsulates a categorized failure and the corresponding recovery directive.
/// </summary>
public sealed record AgentFailureContext(
    FailureTier Tier,
    string ErrorCode,
    string Description,
    bool IsRecoverable,
    string? SuggestedRemediation);

/// <summary>
/// Policy engine for resolving failure remediation actions based on the four-tier hierarchy.
/// </summary>
public static class FailureHierarchyRouter
{
    public static AgentFailureContext Classify(Exception? exception, string? detail, FailureTier defaultTier = FailureTier.Tool)
    {
        string message = exception?.Message ?? detail ?? "Unknown error";
        string lower = message.ToLowerInvariant();

        if (lower.Contains("budget") || lower.Contains("exhausted"))
        {
            return new AgentFailureContext(
                Tier: FailureTier.Goal,
                ErrorCode: "BUDGET_EXHAUSTED",
                Description: message,
                IsRecoverable: false,
                SuggestedRemediation: "Halt execution and report final resource status.");
        }

        if (lower.Contains("unknown_tool") || lower.Contains("hallucinated") || lower.Contains("parse"))
        {
            return new AgentFailureContext(
                Tier: FailureTier.Generation,
                ErrorCode: "MODEL_OUTPUT_INVALID",
                Description: message,
                IsRecoverable: true,
                SuggestedRemediation: "Inject corrective instruction and prompt model for a valid tool call.");
        }

        if (lower.Contains("dependency") || lower.Contains("blocked"))
        {
            return new AgentFailureContext(
                Tier: FailureTier.WorkItem,
                ErrorCode: "WORK_ITEM_BLOCKED",
                Description: message,
                IsRecoverable: true,
                SuggestedRemediation: "Replan or proceed with next independent work item.");
        }

        return new AgentFailureContext(
            Tier: defaultTier,
            ErrorCode: "TOOL_EXECUTION_FAILED",
            Description: message,
            IsRecoverable: true,
            SuggestedRemediation: "Retry with alternative arguments or different tool.");
    }
}
