using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Klydis.Core.Tasks;

/// <summary>
/// Deterministic Prompt Invariant Engine.
/// Generates authoritative prompt obligation blocks reflecting first-class step semantics,
/// allowed tool constraints, and schema-governed planning instructions.
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

    /// <summary>
    /// Builds the planning directive when an objective needs an execution plan from scratch.
    /// Instructs the model to generate a tailored plan with tasks, dependencies, capabilities, and verification.
    /// </summary>
    public static string BuildPlanningPromptDirective(
        string objective,
        IReadOnlyList<string>? availableCapabilities = null,
        WorldState? worldState = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<planning_instruction>");
        sb.AppendLine($"OBJECTIVE: {objective}");

        if (availableCapabilities != null && availableCapabilities.Count > 0)
        {
            sb.AppendLine("AVAILABLE CAPABILITIES:");
            foreach (var cap in availableCapabilities.OrderBy(c => c, StringComparer.Ordinal))
            {
                sb.AppendLine($"  - {cap}");
            }
        }

        if (worldState != null && worldState.Facts.Count > 0)
        {
            sb.AppendLine("CURRENT WORLD STATE:");
            foreach (var kvp in worldState.Facts)
            {
                sb.AppendLine($"  - {kvp.Key}: {kvp.Value}");
            }
        }

        sb.AppendLine("INSTRUCTION:");
        sb.AppendLine("Create an execution plan appropriate to this objective using the 'plan' tool.");
        sb.AppendLine("Do not use a generic workflow template (e.g. standard 'Analyze -> Implement -> Test').");
        sb.AppendLine("Only create tasks that materially contribute to achieving the objective.");
        sb.AppendLine("Define completion criteria that will be verified with execution evidence.");
        sb.Append("</planning_instruction>");

        return sb.ToString();
    }
}
