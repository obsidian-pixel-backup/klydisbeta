using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Klydis.Core.Tasks;

/// <summary>
/// Deterministic Prompt Invariant Engine (Phase 15).
/// Generates authoritative prompt obligation blocks reflecting first-class step semantics
/// and allowed tool constraints.
/// </summary>
public static class PromptInvariantEngine
{
    /// <summary>
    /// Builds the structured prompt directive for a step's action obligation and allowed tools.
    /// </summary>
    public static string BuildStepPromptDirective(TaskStep? step)
    {
        if (step == null) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<current_step_obligation>");
        sb.AppendLine($"Step: {step.Title}");
        sb.AppendLine($"Action Kind: {step.ExpectedActionKind}");

        if (step.AllowedTools != null && step.AllowedTools.Count > 0)
        {
            sb.AppendLine($"Allowed Tools: [{string.Join(", ", step.AllowedTools.OrderBy(t => t, StringComparer.Ordinal))}]");
        }
        else if (step.AllowedTools != null && step.AllowedTools.Count == 0)
        {
            sb.AppendLine("Allowed Tools: [None - Reasoning/Discussion only]");
        }

        if (step.VerificationCriteria != null && step.VerificationCriteria.Count > 0)
        {
            sb.AppendLine($"Verification Criteria: [{string.Join(", ", step.VerificationCriteria)}]");
        }

        sb.Append("</current_step_obligation>");
        return sb.ToString();
    }
}
