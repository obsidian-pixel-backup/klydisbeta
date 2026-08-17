using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// The authoritative specification for what the current step obliges the model to produce
/// (P1.8). Derived ENTIRELY from the current <see cref="TaskStep"/> — never from ad-hoc text
/// matching in the loop. Consumed by:
///   - the correction directive (what to demand of the model),
///   - the Action Gate / ActionValidator (what actions are legal right now),
///   - the prompt (CURRENT STEP / EXPECTED ACTION / ALLOWED TOOLS).
/// </summary>
public sealed record ActionObligation(
    string? StepId,
    string Title,
    StepActionKind ExpectedActionKind,
    IReadOnlySet<string>? AllowedTools,
    IReadOnlyList<string> RequiredEvidence,
    IReadOnlyList<string> ForbiddenActions,
    string? CompletionCondition)
{
    /// <summary>The obligation for a step, or null when there is no current step.</summary>
    public static ActionObligation? FromStep(TaskStep? step)
    {
        if (step == null) return null;
        return new ActionObligation(
            StepId: step.StepId,
            Title: step.Title,
            ExpectedActionKind: step.ExpectedActionKind,
            AllowedTools: step.AllowedTools,
            RequiredEvidence: step.VerificationCriteria,
            ForbiddenActions: new[]
            {
                "Invent tools that are not registered.",
                "Claim tool results without a tool execution.",
                "Claim the step complete without execution evidence.",
                "Narrate the harness or simulate execution in text."
            },
            CompletionCondition: step.CompletionCondition);
    }

    /// <summary>
    /// The tools permitted right now: the step's allowed set, or null when the step declares
    /// no restriction (existence-gated full surface).
    /// </summary>
    public bool IsToolAllowed(string toolName)
        => AllowedTools == null || AllowedTools.Contains(toolName);

    /// <summary>A compact one-line description for diagnostics.</summary>
    public override string ToString()
        => $"{StepId ?? "?"} [{ExpectedActionKind}] '{Title}' tools={(AllowedTools == null ? "all(existence-gated)" : string.Join(",", AllowedTools.OrderBy(n => n, StringComparer.Ordinal)))}";
}
