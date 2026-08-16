using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Klydis.Core.Chat;
using Klydis.Core.Memory;
using Microsoft.Extensions.Logging;
using TaskStatus = Klydis.Core.Chat.TaskStatus;

namespace Klydis.Core.Tasks;

/// <summary>
/// The task runtime boundary. This is where the harness owns execution: task resolution
/// (via <see cref="TaskManager"/>), run lifecycle, generation-outcome classification, and the
/// supervisor decisions that drive the loop. ChatEngine (chat transport + streaming) consults
/// this service for every execution decision instead of owning them. The loop's mechanics
/// still live in ChatEngine for now; this class is the seam that owns WHAT happens next.
/// </summary>
public class AgentRuntime(
    TaskManager taskManager,
    MessageStore store,
    ILogger<AgentRuntime>? logger = null)
{
    private readonly TaskManager _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
    private readonly MessageStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<AgentRuntime>? _logger = logger;
    private readonly ConcurrentDictionary<string, TaskRun> _activeRuns = new();

    /// <summary>
    /// Maps the inference engine's raw end-of-generation flags to a <see cref="GenerationOutcome"/>.
    /// The outcome is a fact about the generation; the supervisor turns it into a decision.
    /// </summary>
    public GenerationOutcome ClassifyGeneration(
        bool hitMaxTokens,
        bool cutShortMidStream,
        bool cancelled,
        bool endedOnOwnStop,
        bool promptFilledWindow,
        bool visibleEmpty,
        bool noActionProduced = false)
    {
        if (cancelled) return GenerationOutcome.Cancelled;
        if (promptFilledWindow) return GenerationOutcome.ContextExhausted;
        if (hitMaxTokens) return GenerationOutcome.OutputBudgetExhausted;
        if (cutShortMidStream) return GenerationOutcome.GenerationCutShort;
        // A text-only autonomous response is a protocol failure, not a completed turn — and
        // it is more informative than "model ended early", so it takes precedence: the
        // supervisor must route it to RepairProtocol, never to a silent completion.
        if (noActionProduced) return GenerationOutcome.NoActionProduced;
        if (endedOnOwnStop) return GenerationOutcome.ModelEndedEarly;
        if (visibleEmpty) return GenerationOutcome.DegenerateLoop;
        return GenerationOutcome.CompletedTurn;
    }

    /// <summary>
    /// Opens a new run for the task (one continuous execution attempt). Returns the run; a
    /// subsequent <see cref="EndRunAsync"/> closes it. Persisted so a restart can reconstruct
    /// the execution hierarchy.
    /// </summary>
    public async Task<TaskRun> EnsureRunAsync(string taskId)
    {
        // A Run is one CONTINUOUS execution attempt of a task, spanning many user turns and
        // model generations (Task → Run → Turn). If the task already has an open running
        // run, reuse it and bump the turn counter instead of creating a per-turn run — the
        // old behavior opened and immediately cancelled a fresh run around every user turn,
        // which made telemetry say "Run cancelled" whenever the model simply stopped early.
        if (_activeRuns.TryGetValue(taskId, out var existing) && existing.Status == RunStatus.Running)
        {
            var bumped = existing with { TurnCount = existing.TurnCount + 1 };
            _activeRuns[taskId] = bumped;
            try
            {
                await _store.SaveRunAsync(bumped);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to persist run continuation for task {TaskId}.", taskId);
            }
            _logger?.LogDebug("Run {RunId} continues for task {TaskId} (turn {Turn}).", bumped.RunId, taskId, bumped.TurnCount);
            return bumped;
        }

        var run = new TaskRun(
            RunId: "R-" + Guid.NewGuid().ToString("N")[..12],
            TaskId: taskId,
            StartedAtUtc: DateTime.UtcNow,
            EndedAtUtc: null,
            Status: RunStatus.Running,
            TurnCount: 1);
        _activeRuns[taskId] = run;
        try
        {
            await _store.SaveRunAsync(run);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist run start for task {TaskId}.", taskId);
        }
        _logger?.LogInformation("Run {RunId} started for task {TaskId}.", run.RunId, taskId);
        return run;
    }

    /// <summary>
    /// Closes the task's active run with the given terminal status.
    /// </summary>
    public async Task EndRunAsync(string taskId, RunStatus status)
    {
        if (!_activeRuns.TryRemove(taskId, out var run)) return;
        var ended = run with { Status = status, EndedAtUtc = DateTime.UtcNow };
        try
        {
            await _store.SaveRunAsync(ended);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist run end for task {TaskId}.", taskId);
        }
        _logger?.LogInformation("Run {RunId} ended for task {TaskId} with status {Status}.", run.RunId, taskId, status);
    }

    /// <summary>
    /// Runs the deterministic completion gate on a task_complete claim. The model's claim is
    /// merely an input; the gate decides. Accepted ⇒ the task is marked Completed (through the
    /// state machine), rejected ⇒ the reason is returned and the loop continues.
    /// </summary>
    public async Task<(bool Accepted, string? Reason)> EvaluateCompletionClaimAsync(
        string taskId,
        IReadOnlyList<ToolExecutor.PlanEntry> plan,
        string? summary)
    {
        var verdict = AgentSupervisor.EvaluateCompletion(plan.Where(e => !e.Done).Select(e => e.Text).ToList());
        if (verdict.Accepted)
        {
            await CompleteTaskAsync(taskId, summary);
        }
        return (verdict.Accepted, verdict.Reason);
    }

    /// <summary>
    /// Marks the task Completed through the guarded state machine (Running → Completed), with
    /// the model's completion summary. A transition that is not legal (e.g. already terminal)
    /// is a no-op.
    /// </summary>
    public async Task CompleteTaskAsync(string taskId, string? summary)
    {
        var task = await _taskManager.GetTaskAsync(taskId);
        if (task == null) return;
        var completed = TaskStateMachine.TryTransition(task, TaskStatus.Completed);
        if (completed == null)
        {
            _logger?.LogDebug("Completion transition from {From} is not legal for task {TaskId}; no-op.", task.Status, taskId);
            return;
        }
        completed = completed with { Summary = summary };
        await _taskManager.SaveTaskAsync(completed);
        _logger?.LogInformation("Task {TaskId} marked Completed by the supervisor.", taskId);
    }

    /// <summary>
    /// The supervisor's decision after a generation, evaluated against the live durable state.
    /// Pure decision; the caller implements the mechanics.
    /// </summary>
    public Task<SupervisorDecision> DecideAfterTurnAsync(
        string taskId,
        GenerationOutcome outcome,
        bool claimAccepted,
        IReadOnlyList<ToolExecutor.PlanEntry> plan,
        int pendingQueueItems,
        int completionRejections,
        int maxCompletionRejections = 3,
        int consecutiveStalledTurns = 0,
        int maxStalledTurns = 6)
    {
        // The task's durable state (plan/queue) is passed in by the caller; the task id is
        // kept on the signature for the checkpoint phase, when the runtime will read step and
        // run state itself rather than receiving snapshots.
        return Task.FromResult(AgentSupervisor.DecideAfterTurn(
            claimAccepted, outcome, plan, pendingQueueItems,
            completionRejections, maxCompletionRejections,
            consecutiveStalledTurns, maxStalledTurns));
    }
}
