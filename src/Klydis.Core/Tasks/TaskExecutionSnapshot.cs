using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// The aggregate of a task's CURRENT execution state (P1.8) — the only input to the
/// supervisor's decision. Everything the supervisor needs to answer \"continue? complete?
/// repair? replan? pause?\" is here: the task, run, current step, plan, queue, the generation
/// outcome, the factual state delta, and the rejection/stall counters. The model's narrative
/// is NOT part of this; only observable, durable facts are.
/// </summary>
public sealed record TaskExecutionSnapshot(
    string? TaskId,
    string? TaskObjective,
    string? RunId,
    TaskStep? CurrentStep,
    IReadOnlyList<ToolExecutor.PlanEntry> Plan,
    int PendingQueueItems,
    GenerationOutcome Outcome,
    StateDelta StateDelta,
    int CompletionRejections,
    int ConsecutiveStalledTurns,
    bool CompletionClaimAccepted = false)
{
    /// <summary>Number of plan items still open — the completion gate's core fact.</summary>
    public int OpenPlanItems => Plan.Count(e => !e.Done);

    /// <summary>True when factual state changed (tools ran, plan moved, files changed).</summary>
    public bool MadeFactualProgress => StateDelta is { IsEmpty: false };

    /// <summary>True when this generation produced a parseable tool call.</summary>
    public bool ProducedToolCall => Outcome == GenerationOutcome.ToolCallProduced;

    /// <summary>The text of the current step, or null.</summary>
    public string? CurrentStepText => CurrentStep?.Title;
}
