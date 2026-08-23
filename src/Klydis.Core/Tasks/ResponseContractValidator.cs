using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Classification of autonomous model generation compliance against the execution contract.
/// </summary>
public enum ResponseContractClassification
{
    /// <summary>Output contains an executable tool call action.</summary>
    ValidAction,

    /// <summary>Output contains a plan creation or plan mutation.</summary>
    PlanUpdate,

    /// <summary>Output contains a formal completion claim.</summary>
    CompletionClaim,

    /// <summary>Output is waiting for user input or reports an external blocker.</summary>
    UserBlocker,

    /// <summary>Output requests replanning or alternative strategy.</summary>
    ReplanRequest,

    /// <summary>Model falsely claims lack of capability/authority to run commands or use tools.</summary>
    CapabilityRefusal,

    /// <summary>Model produced conversational narration without an executable action.</summary>
    NoActionNarration,

    /// <summary>Model produced degenerate empty or repetitive loops.</summary>
    DegenerateLoop
}

/// <summary>
/// Result of evaluating model response compliance against the autonomous contract.
/// </summary>
public sealed record ResponseContractVerdict(
    ResponseContractClassification Classification,
    bool IsCompliant,
    string? RefusalPattern,
    string? RecommendedDirective);

/// <summary>
/// Validates autonomous turns against the strict response contract (P0/P1).
/// Guarantees that narrative claims and capability refusals are intercepted
/// deterministically before wasting generations.
/// </summary>
public static class ResponseContractValidator
{
    private static readonly Regex CapabilityRefusalRegex = new(
        @"(?:i\s+(?:cannot|can't|am\s+unable\s+to|do\s+not\s+have\s+the\s+ability\s+to|am\s+not\s+able\s+to)\s+(?:run|execute|perform|access|interact|modify|view|check|open|launch)\s+(?:commands?|tools?|scripts?|code|files?|terminal|system|hardware|powershell|bash|cmd|processes))|" +
        @"(?:as\s+an\s+ai(?:\s+language\s+model)?,\s+i\s+(?:cannot|can't|do\s+not\s+have\s+access))|" +
        @"(?:i\s+will\s+wait\s+for\s+you\s+to\s+(?:provide|run|execute|allow))|" +
        @"(?:i\s+don't\s+have\s+(?:access\s+to\s+(?:the\s+)?terminal|permission\s+to\s+execute))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Evaluates raw output and parsed tool calls against the autonomous response contract.
    /// </summary>
    public static ResponseContractVerdict Evaluate(
        string? rawOutput,
        IReadOnlyList<ToolCallRequest>? parsedActions,
        TaskStep? currentStep = null)
    {
        if (parsedActions != null && parsedActions.Count > 0)
        {
            if (parsedActions.Any(a => string.Equals(a.Name, "task_complete", StringComparison.OrdinalIgnoreCase)))
            {
                return new ResponseContractVerdict(ResponseContractClassification.CompletionClaim, true, null, null);
            }

            if (parsedActions.Any(a => string.Equals(a.Name, "plan", StringComparison.OrdinalIgnoreCase)))
            {
                return new ResponseContractVerdict(ResponseContractClassification.PlanUpdate, true, null, null);
            }

            return new ResponseContractVerdict(ResponseContractClassification.ValidAction, true, null, null);
        }

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return new ResponseContractVerdict(
                ResponseContractClassification.DegenerateLoop,
                false,
                null,
                "The generation produced no tokens or empty output. Please generate the required action.");
        }

        string trimmed = rawOutput.Trim();

        // Check for capability refusal patterns
        var refusalMatch = CapabilityRefusalRegex.Match(trimmed);
        if (refusalMatch.Success)
        {
            string directive = DeterministicDirectiveEngine.BuildCapabilityRefusalDirective(currentStep);
            return new ResponseContractVerdict(
                ResponseContractClassification.CapabilityRefusal,
                false,
                refusalMatch.Value,
                directive);
        }

        // Check for replan / blocker keywords
        if (trimmed.StartsWith("[REPLAN]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("REPLAN:", StringComparison.OrdinalIgnoreCase))
        {
            return new ResponseContractVerdict(ResponseContractClassification.ReplanRequest, true, null, null);
        }

        if (trimmed.StartsWith("[BLOCKED]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("USER_INPUT_REQUIRED:", StringComparison.OrdinalIgnoreCase))
        {
            return new ResponseContractVerdict(ResponseContractClassification.UserBlocker, true, null, null);
        }

        // Text narration without actions
        string narrationDirective = DeterministicDirectiveEngine.BuildNoActionNarrationDirective(currentStep);
        return new ResponseContractVerdict(
            ResponseContractClassification.NoActionNarration,
            false,
            null,
            narrationDirective);
    }
}
