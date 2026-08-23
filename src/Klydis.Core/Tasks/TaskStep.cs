using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// What kind of action a step expects the model to produce. This is the step's contract with
/// the action layer: the ActionObligation derives from it, the correction directive switches
/// on it, and (via AllowedTools) the Action Gate enforces it. Never inferred from English in
/// the loop — it is produced once, deterministically, by <see cref="StepClassifier"/> (the
/// single owner of step semantics until steps are persisted with their own metadata).
/// </summary>
public enum StepActionKind
{
    /// <summary>No specific action expected (reasoning/analysis only).</summary>
    None,

    /// <summary>Produce reasoning/requirements (no mandatory tool).</summary>
    Reason,

    /// <summary>Inspect the workspace (filesystem tools).</summary>
    Inspect,

    /// <summary>External research (web search / crawl).</summary>
    Research,

    /// <summary>Produce or revise a plan.</summary>
    Plan,

    /// <summary>Mutate project files (read/write/edit).</summary>
    FileMutation,

    /// <summary>Run commands against the environment.</summary>
    CommandExecution,

    /// <summary>Interact with a terminal/preview session.</summary>
    TerminalInteraction,

    /// <summary>Verify the result with evidence (build/tests/preview).</summary>
    Verification,

    /// <summary>Wait for user input.</summary>
    UserInput,

    /// <summary>Produce the final deliverable.</summary>
    Summary
}

/// <summary>
/// Execution retry policy for a task step.
/// </summary>
public sealed record StepRetryPolicy(
    int MaxAttempts = 3,
    int BackoffSeconds = 0,
    bool FailTaskOnMaxRetries = true);

/// <summary>
/// A first-class, durable execution step (P1.8). Replaces the raw plan-text heuristic model:
/// the supervisor, the correction directive, and the Action Gate all consume these records —
/// never ad-hoc phrase matching in the loop. Built from the persisted plan checklist via
/// <see cref="TaskStepBuilder"/> (the checklist remains the durable backbone; the semantics
/// are derived once through <see cref="StepClassifier"/>, the single owner of step meaning).
/// </summary>
public sealed record TaskStep(
    string StepId,
    string? TaskId,
    int Order,
    string Title,
    TaskStepStatus Status,
    StepActionKind ExpectedActionKind,
    IReadOnlySet<string>? AllowedTools,
    IReadOnlyList<string> RequiredSkills,
    IReadOnlyList<string> ExpectedArtifacts,
    IReadOnlyList<string> VerificationCriteria,
    string? CompletionCondition,
    int AttemptCount,
    string? LastActionId,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? RunId = null,
    string? ParentStepId = null,
    string? FailureReason = null,
    int Version = 1,
    IReadOnlyList<string>? Dependencies = null,
    IReadOnlyList<string>? ExpectedFiles = null,
    StepRetryPolicy? RetryPolicy = null)
{
    /// <summary>True when the step is open (not completed or skipped).</summary>
    public bool IsOpen => Status is not (TaskStepStatus.Completed or TaskStepStatus.Skipped);

    /// <summary>A stable step id derived from the task and order: {TaskId}-S{Order:000}.</summary>
    public static string BuildStepId(string? taskId, int order)
        => string.IsNullOrEmpty(taskId) ? $"S{order:000}" : $"{taskId}-S{order:000}";
}

/// <summary>
/// Builds first-class <see cref="TaskStep"/> records from the persisted plan checklist. The
/// plan entries remain the durable source of truth (text + done flag, stored per task/session);
/// this is the single place that lifts them into execution semantics, so every consumer
/// (supervisor, obligation, gate, directive) sees the same step model.
/// </summary>
public static class TaskStepBuilder
{
    /// <summary>Builds the full step list for a plan, in plan order.</summary>
    public static IReadOnlyList<TaskStep> Build(
        IReadOnlyList<ToolExecutor.PlanEntry> plan,
        string? taskId)
    {
        if (plan == null || plan.Count == 0) return Array.Empty<TaskStep>();
        var steps = new List<TaskStep>(plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            steps.Add(FromPlanEntry(plan[i], i, taskId));
        }
        return steps;
    }

    /// <summary>Builds one step from a plan entry.</summary>
    public static TaskStep FromPlanEntry(
        ToolExecutor.PlanEntry entry,
        int order,
        string? taskId)
    {
        var c = StepClassifier.Classify(entry.Text);
        return new TaskStep(
            StepId: TaskStep.BuildStepId(taskId, order),
            TaskId: taskId,
            Order: order,
            Title: entry.Text,
            Status: entry.Done ? TaskStepStatus.Completed : TaskStepStatus.Pending,
            ExpectedActionKind: c.ExpectedActionKind,
            AllowedTools: c.AllowedTools,
            RequiredSkills: c.RequiredSkills,
            ExpectedArtifacts: c.ExpectedArtifacts,
            VerificationCriteria: c.VerificationCriteria,
            CompletionCondition: c.CompletionCondition,
            AttemptCount: 0,
            LastActionId: null,
            StartedAt: null,
            CompletedAt: entry.Done ? DateTime.UtcNow : null);
    }

    /// <summary>The first open step, or null when every step is done (or there is no plan).</summary>
    public static TaskStep? CurrentStep(IReadOnlyList<TaskStep> steps)
        => steps.FirstOrDefault(s => s.IsOpen);
}
