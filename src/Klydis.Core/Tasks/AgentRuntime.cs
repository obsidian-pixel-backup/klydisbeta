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

    // P1.12: the run-scoped evidence ledger (workspace-versioned invalidation + decision
    // records), DURABLE (review §2/§15): every evidence row and supervisor decision is
    // persisted to the store, and a fresh run rehydrates the task's surviving evidence so a
    // crash cannot erase a recorded BuildPassed. Keyed by task; reset when a FRESH run
    // starts, kept while a run continues so evidence survives user turns within the run.
    private readonly ExecutionEvidenceLedger _evidenceLedger = new(store);

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
                // P0.8: run persistence failures surface. The task-resolution caller fails
                // closed (no task scoping, no tools this turn) rather than proceeding as if
                // the run were durably recorded.
                _logger?.LogError(ex, "Failed to persist run continuation for task {TaskId}; the run is NOT durably recorded.", taskId);
                throw;
            }
            _logger?.LogDebug("Run {RunId} continues for task {TaskId} (turn {Turn}).", bumped.RunId, taskId, bumped.TurnCount);
            return bumped;
        }

        // RUN RECOVERY (restart safety): no run is active in this process, so check the
        // durable store for a run a PREVIOUS process left open (Running/Suspended). The
        // process ownership of that run was lost — mark it Interrupted (the durable
        // hierarchy shows the interruption) and start a fresh resumable run. Without this,
        // every restart created a brand-new run while the database still showed a Running
        // run — Task → Run → Turn was not recoverable across process boundaries.
        try
        {
            var stale = await _store.GetRunsAsync(taskId);
            var open = stale
                .Where(r => r.Status == RunStatus.Running || r.Status == RunStatus.Suspended)
                .OrderByDescending(r => r.StartedAtUtc)
                .FirstOrDefault();
            if (open != null)
            {
                var interrupted = open with
                {
                    Status = RunStatus.Interrupted,
                    EndedAtUtc = DateTime.UtcNow
                };
                await _store.SaveRunAsync(interrupted);
                // REVIEW §10: actions the dead process left InProgress are now UNKNOWN — we
                // do NOT know whether they completed, failed, or are still running. The
                // durable action ledger says so, so recovery inspects (process/filesystem/
                // lockfile) instead of blindly re-executing the same action.
                try
                {
                    await _store.MarkInProgressActionsUnknownAsync(open.RunId, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to mark interrupted run {RunId}'s in-flight actions Unknown.", open.RunId);
                }
                _logger?.LogWarning(
                    "Run {RunId} for task {TaskId} was left {Status} by a previous process; " +
                    "marked Interrupted (in-flight actions marked Unknown) and starting a fresh resumable run.",
                    open.RunId, taskId, open.Status);
            }
        }
        catch (Exception ex)
        {
            // FAIL CLOSED (review §10): a recovery scan failure means we do NOT know whether
            // another execution history is Running for this task. Continuing would create a
            // COMPETING run (database shows Running, memory adds another Running) — two
            // histories can then both execute the same task. Persistence/recovery
            // unavailable ⇒ surface the error and let the caller pause the task; never
            // create a competing run.
            _logger?.LogError(ex, "Run recovery scan FAILED for task {TaskId}: the durable run " +
                "state could not be read. Refusing to start a possibly-competing run.", taskId);
            throw;
        }

        // A fresh run starts a fresh execution ledger — evidence and decisions are scoped to
        // the run, never leaked across attempts.
        _evidenceLedger.Reset(taskId);

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
            // P0.8: see run-continuation branch above — surface, never swallow.
            _logger?.LogError(ex, "Failed to persist run start for task {TaskId}; the run is NOT durably recorded.", taskId);
            throw;
        }
        _logger?.LogInformation("Run {RunId} started for task {TaskId}.", run.RunId, taskId);
        return run;
    }

    /// <summary>
    /// Closes the task's active run with the given terminal status.
    /// </summary>
    /// <summary>
    /// The id of the task's active run, or null when the task has no open run. Used for
    /// diagnostics so every action rejection carries TaskId + RunId + StepId context.
    /// </summary>
    public string? GetActiveRunId(string taskId)
        => _activeRuns.TryGetValue(taskId, out var run) ? run.RunId : null;

    /// <summary>The active run's turn identity ({RunId}#T{TurnCount}), or null when no run is
    /// open — stamped onto every durable action record so recovery can group actions by turn.</summary>
    public string? GetActiveTurnId(string taskId)
        => _activeRuns.TryGetValue(taskId, out var run) ? $"{run.RunId}#T{run.TurnCount}" : null;

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
            // P0.8: surface, never swallow. Callers that must not crash on a telemetry write
            // (e.g. the turn-ending finally in ChatEngine) guard the call explicitly.
            _logger?.LogError(ex, "Failed to persist run end for task {TaskId}; the run termination was NOT durable.", taskId);
            throw;
        }
        _logger?.LogInformation("Run {RunId} ended for task {TaskId} with status {Status}.", run.RunId, taskId, status);
    }

    /// <summary>
    /// Records typed evidence into the run ledger (P1.12), stamped with the workspace version
    /// it was produced against. A later file change invalidates it. The run/action ids give
    /// the durable row its lineage (review §2).
    /// </summary>
    public void RecordRunEvidence(string taskId, Evidence evidence, string? runId = null, string? actionId = null)
        => _evidenceLedger.RecordEvidence(taskId, evidence, runId, actionId);

    /// <summary>Bumps the run's workspace version — every prior build/preview evidence entry
    /// is now STALE (file changes invalidate verification).</summary>
    public void NoteRunFileChanged(string taskId)
        => _evidenceLedger.NoteFileChanged(taskId);

    /// <summary>The run's current workspace version (0 = no files changed yet).</summary>
    public int GetRunWorkspaceVersion(string taskId)
        => _evidenceLedger.GetWorkspaceVersion(taskId);

    /// <summary>The run's CURRENT (non-stale) evidence.</summary>
    public IReadOnlyList<EvidenceLedgerEntry> GetRunEvidence(string taskId)
        => _evidenceLedger.GetCurrentEvidence(taskId);

    /// <summary>
    /// Builds the completion eligibility for the task RIGHT NOW (P0 — the checklist gate's
    /// second dimension): every step complete, every verification step's predicate satisfied
    /// by CURRENT run evidence, and no unresolved verification failures.
    /// </summary>
    public CompletionEligibility BuildCompletionEligibility(
        string taskId,
        IReadOnlyList<ToolExecutor.PlanEntry> plan)
        => AgentSupervisor.EvaluateEligibility(plan, taskId, _evidenceLedger.GetCurrentEvidence(taskId));

    /// <summary>
    /// Records the supervisor's decision against the run (decision ledger; P1.12 Phase A).
    /// Called by <see cref="DispatchAsync"/> for every dispatched decision — and by the
    /// tool loop's accepted-completion path, so CompleteTask is on the same audit trail as
    /// every other decision. In-memory + logged for now; the durable store write lands with
    /// the persistence milestone.
    /// </summary>
    public void RecordRunDecision(string taskId, SupervisorDecision decision)
    {
        var record = new ExecutionDecisionRecord(
            DecisionId: "D-" + Guid.NewGuid().ToString("N")[..12],
            TaskId: taskId,
            RunId: GetActiveRunId(taskId),
            StepId: decision.NextStepId,
            Decision: decision.Decision,
            Reason: decision.Reason,
            TimestampUtc: DateTime.UtcNow);
        _evidenceLedger.RecordDecision(taskId, record);
        _logger?.LogInformation("ExecutionDecision recorded: task={TaskId} decision={Decision} reason={Reason} step={Step}",
            taskId, decision.Decision, decision.Reason, decision.NextStepId ?? "—");
    }

    /// <summary>
    /// The task workspace root (review §12) — established by the app when a project directory
    /// is known, propagated through the action-validations context so the boundary validator
    /// enforces containment for every filesystem action. Null = permissive (no task workspace
    /// concept yet), preserving current behavior.
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>The runtime's established task workspace, or null when none is set.</summary>
    public TaskWorkspace? CurrentWorkspace
        => string.IsNullOrWhiteSpace(WorkspaceRoot) ? null : new TaskWorkspace(WorkspaceRoot);

    // ===== Durable action ledger (review §9–§10) ============================================

    /// <summary>
    /// Records an action as InProgress in the durable action ledger, BEFORE the tool
    /// executor runs it. The returned action id correlates the start with the completion
    /// record (and with any gate rejection for the same call). The loop calls this once per
    /// executed tool call; a process death leaves the row InProgress, which recovery marks
    /// Unknown (see <see cref="MessageStore.MarkInProgressActionsUnknownAsync"/>).
    /// </summary>
    /// <returns>The durable action id, or NULL when the durable record could not be
    /// written. A null return is FAIL-CLOSED: the caller must NOT execute the action — a
    /// real side effect must never occur without its durable Prepared/InProgress record.
    /// The previous behavior swallowed the persistence failure and executed anyway, which
    /// produced side effects with no durable audit trail (and an InProgress row that
    /// recovery would then mark Unknown for an action that actually ran).</returns>
    public string? RecordRunActionStart(
        string? taskId,
        string? runId,
        string? stepId,
        string turnId,
        ToolCallRequest request,
        int turnActionOrdinal,
        string? modelId = null,
        string? protocolKey = null)
    {
        string actionId = ActionGate.ComputeActionId(request, taskId, runId, turnActionOrdinal);
        var record = new TaskActionRecord(
            ActionId: actionId,
            ReplayKey: ActionGate.ComputeReplayKey(request),
            TaskId: taskId,
            RunId: runId,
            StepId: stepId,
            TurnId: turnId,
            ToolName: request.Name,
            ArgumentsJson: SerializeArguments(request.Arguments),
            SideEffectLevel: ToolSideEffectClassifier.Classify(request.Name),
            Status: ActionExecutionStatus.InProgress,
            StartedAtUtc: DateTime.UtcNow,
            ModelId: modelId,
            ProtocolKey: protocolKey);
        try
        {
            _store.SaveTaskActionAsync(record).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist action start {ActionId}. Action MUST NOT execute without its durable record.", actionId);
            return null;
        }
        return actionId;
    }

    /// <summary>
    /// Completes an action in the durable ledger with its final status and result. Called
    /// after the tool executor returns (or the call is cancelled/times out). Idempotent by
    /// action id — a completion retry rewrites the same row.
    /// </summary>
    /// <remarks>P0: a null/empty action id (the start record was never durably written) is
    /// a no-op — there is no row to complete. A persistence failure here leaves the row
    /// InProgress, which recovery marks Unknown rather than silently treating the action as
    /// succeeded/failed; the failure is logged at ERROR so the inconsistency is visible.</remarks>
    public void RecordRunActionComplete(
        string? actionId,
        ActionExecutionStatus status,
        string? resultPreview = null,
        string? error = null)
    {
        if (string.IsNullOrEmpty(actionId)) return;
        try
        {
            var existing = _store.GetTaskActionAsync(actionId).GetAwaiter().GetResult();
            if (existing == null) return;
            var completed = existing with
            {
                Status = status,
                CompletedAtUtc = DateTime.UtcNow,
                ResultPreview = Truncate(resultPreview, 2000),
                Error = Truncate(error, 2000)
            };
            _store.SaveTaskActionAsync(completed).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist action completion {ActionId}; the row remains InProgress and will be marked Unknown on recovery.", actionId);
        }
    }

    /// <summary>The durable action records for a task (all runs), oldest first — the ledger
    /// recovery and diagnostics read.</summary>
    public async Task<IReadOnlyList<TaskActionRecord>> GetTaskActionsAsync(string? taskId, string? runId = null)
        => await _store.GetTaskActionsAsync(taskId, runId);

    /// <summary>
    /// The replay keys of actions that MUST NOT be re-executed (review §9: replay protection
    /// is a durable Action Ledger, not process memory): actions whose outcome is known
    /// Succeeded, or Unknown (they may have landed — re-running them could duplicate side
    /// effects). Only actions from runs that did NOT complete cleanly count — a reopened
    /// task after a cleanly Completed run is a genuinely fresh attempt where redo is allowed.
    /// ChatEngine seeds its in-memory executed-set from this on every run change, so replay
    /// protection survives restarts.
    /// </summary>
    public async Task<HashSet<string>> GetExecutedReplayKeysAsync(string taskId)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(taskId)) return keys;
        // P0 fail-closed: a hydration failure must PROPAGATE, never silently return an empty
        // set. "We failed to read the ledger" must not mean "nothing executed" — the caller
        // treats a thrown hydration as reason to refuse tool execution for the turn (see
        // ChatEngine's replay-seeding block).
        var runs = await _store.GetRunsAsync(taskId);
        var nonCompletedRuns = runs
            .Where(r => r.Status != RunStatus.Completed)
            .Select(r => r.RunId)
            .ToHashSet(StringComparer.Ordinal);
        var actions = await _store.GetTaskActionsAsync(taskId, null);
        foreach (var a in actions)
        {
            if (a.RunId != null && !nonCompletedRuns.Contains(a.RunId)) continue;
            if (a.ReplayKey != null &&
                (a.Status == ActionExecutionStatus.Succeeded || a.Status == ActionExecutionStatus.Unknown))
            {
                keys.Add(a.ReplayKey);
            }
        }
        return keys;
    }

    // ===== Durable typed-step mirror (review §3) ============================================

    /// <summary>
    /// Persists the typed TaskStep mirror for a task — the derived execution semantics of the
    /// plan (kind, allowed tools, criteria, status) written whenever the plan changes, so
    /// step metadata survives restarts and recovery can read it without re-deriving from
    /// English text. Written only when the plan actually changed (callers pass the current
    /// plan entries; the store round-trips the built steps).
    /// </summary>
    public async Task PersistStepsAsync(string? taskId, IReadOnlyList<ToolExecutor.PlanEntry> plan)
    {
        if (string.IsNullOrEmpty(taskId) || plan == null) return;
        try
        {
            var steps = TaskStepBuilder.Build(plan, taskId);
            await _store.SaveTaskStepsAsync(taskId, steps);
        }
        catch (Exception ex)
        {
            // P0: the runtime must not execute under newly derived step semantics while the
            // durable typed-step mirror is stale or absent. Rethrow so the supervisor path
            // fails the turn (dispatch fail-closed) instead of silently diverging from the
            // durable record that recovery would read after a restart.
            _logger?.LogError(ex, "Failed to persist typed steps for task {TaskId}; the step mirror was NOT durable.", taskId);
            throw;
        }
    }

    /// <summary>The task's persisted typed steps in plan order (empty when none were saved).</summary>
    public async Task<IReadOnlyList<TaskStep>> GetPersistedStepsAsync(string? taskId)
        => string.IsNullOrEmpty(taskId) ? Array.Empty<TaskStep>() : await _store.GetTaskStepsAsync(taskId);

    // ===== Durable decision/evidence reads ==================================================

    /// <summary>The run's durable supervisor-decision history (review §15 audit trail).</summary>
    public async Task<IReadOnlyList<ExecutionDecisionRecord>> GetRunDecisionsAsync(string? taskId, string? runId = null)
        => await _store.GetExecutionDecisionsAsync(taskId, runId);

    /// <summary>The task's durable evidence rows (current or all — review §2).</summary>
    public async Task<IReadOnlyList<DurableEvidenceRecord>> GetRunEvidenceAsync(string? taskId, string? runId = null)
        => await _store.GetExecutionEvidenceAsync(taskId, runId);

    // ===== Per-model capability telemetry (agent-intelligence stage §3) ====================

    /// <summary>
    /// The aggregated per-(model, protocol) execution metrics across ALL tasks, computed from
    /// the durable ledger (actions + typed steps + runs). This is the measurement layer the
    /// capability profiles and the model router are built on — the App surfaces it in the
    /// model library, and it accrues automatically as tasks execute.
    /// </summary>
    public async Task<IReadOnlyList<Orchestration.ModelExecutionMetrics>> BuildExecutionMetricsAsync()
    {
        try
        {
            var actions = await _store.GetAllTaskActionsAsync();
            if (actions.Count == 0) return Array.Empty<Orchestration.ModelExecutionMetrics>();

            var stepKinds = new Dictionary<string, Klydis.Core.Tasks.StepActionKind>(StringComparer.Ordinal);
            foreach (var taskId in actions
                         .Where(a => !string.IsNullOrEmpty(a.TaskId))
                         .Select(a => a.TaskId!)
                         .Distinct(StringComparer.Ordinal))
            {
                try
                {
                    foreach (var step in await _store.GetTaskStepsAsync(taskId))
                    {
                        stepKinds[step.StepId] = step.ExpectedActionKind;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to load typed steps for telemetry (task {TaskId}).", taskId);
                }
            }

            var runs = await _store.GetAllRunsAsync();
            var runOutcomes = runs
                .Select(r => (r.RunId, Completed: r.Status == Klydis.Core.Tasks.RunStatus.Completed))
                .ToList();

            return Orchestration.ExecutionTelemetryAnalyzer.AnalyzeAll(actions, stepKinds, runOutcomes);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to build execution metrics; returning empty telemetry.");
            return Array.Empty<Orchestration.ModelExecutionMetrics>();
        }
    }

    /// <summary>
    /// The empirical capability profile per (model, protocol) — telemetry smoothed toward a
    /// conservative prior. <paramref name="priorResolver"/> lets the caller seed each model's
    /// prior from its <see cref="Klydis.Core.Protocol.ModelProfile"/> (family knowledge); when
    /// null, the default conservative prior is used (no evidence = no claimed capability).
    /// </summary>
    public async Task<IReadOnlyList<Orchestration.ModelCapabilityProfile>> BuildCapabilityProfilesAsync(
        Func<string, Klydis.Core.Protocol.ModelProfile?>? priorResolver = null)
    {
        var metrics = await BuildExecutionMetricsAsync();
        var profiles = new List<Orchestration.ModelCapabilityProfile>(metrics.Count);
        foreach (var m in metrics)
        {
            var prior = priorResolver?.Invoke(m.ModelId) is { } profile
                ? Orchestration.ModelCapabilityEstimator.PriorFromProfile(profile)
                : null;
            profiles.Add(Orchestration.ModelCapabilityEstimator.Estimate(m.ModelId, m.ProtocolKey, m, prior));
        }
        return profiles;
    }

    private static string SerializeArguments(IDictionary<string, object>? args)
    {
        if (args == null || args.Count == 0) return string.Empty;
        try
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in args)
            {
                var val = ToolExecutor.UnwrapJsonElement(kvp.Value);
                dict[kvp.Key] = val?.ToString() ?? string.Empty;
            }
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length <= max ? text : text[..max] + "…[truncated]";
    }

    /// <summary>
    /// P1.15 (Phase A) — the SINGLE dispatcher. Every supervisor decision is executed here:
    /// the durable half (decision ledger record + legal task-state transition) happens in the
    /// runtime, and the returned <see cref="DispatchDirective"/> is what the loop renders.
    /// ChatEngine has no second branch tree — it calls this exactly once per decision and
    /// executes the directive.
    ///
    /// The decision→directive mapping is pure (<see cref="ExecutionDispatcher.BuildDirective"/>);
    /// this method adds the authoritative side effects:
    ///   - CompleteTask           → task sealed Completed through the state machine
    ///   - FailTask               → task transitioned Failed (durable, resumable via reopen)
    ///   - Pause                  → task transitioned Paused (resumable — TaskManager
    ///                              re-activates it to Running on the next turn)
    ///   - Verify                 → the completion gate runs against the run's CURRENT
    ///                              evidence; satisfied ⇒ upgraded to CompleteTask and sealed,
    ///                              unsatisfied ⇒ the model is instructed to produce the
    ///                              missing evidence (the old "special path" that did nothing)
    ///   - ContinueStep/RepairProtocol/Replan/AwaitUser → durable recording + directive
    /// </summary>
    public async Task<DispatchDirective> DispatchAsync(
        SupervisorDecision decision,
        TaskExecutionSnapshot snapshot)
    {
        string? taskId = snapshot.TaskId;

        // Verify: the completion gate decides BEFORE any transition. Satisfied ⇒ the runtime
        // seals the task (completion is runtime-owned — the model's claim is not needed).
        // Unsatisfied ⇒ the directive tells the model what evidence is missing.
        if (decision.Decision == ExecutionDecision.Verify && !string.IsNullOrEmpty(taskId))
        {
            var eligibility = BuildCompletionEligibility(taskId, snapshot.Plan);
            if (eligibility is { AllRequiredStepsComplete: true, AllVerificationPredicatesSatisfied: true, NoUnresolvedFailures: true })
            {
                var sealedDecision = new SupervisorDecision(ExecutionDecision.CompleteTask, ContinuationReason.CompletionAccepted);
                RecordRunDecision(taskId, sealedDecision);
                await CompleteTaskAsync(taskId, null);
                return ExecutionDispatcher.BuildDirective(sealedDecision, snapshot);
            }
            RecordRunDecision(taskId, decision);
            return ExecutionDispatcher.BuildDirective(decision, snapshot, eligibility);
        }

        // Every other decision: record it, execute its durable transition, return the directive.
        if (!string.IsNullOrEmpty(taskId))
        {
            RecordRunDecision(taskId, decision);
        }
        switch (decision.Decision)
        {
            case ExecutionDecision.CompleteTask:
                await CompleteTaskAsync(taskId ?? string.Empty, null);
                break;
            case ExecutionDecision.FailTask:
                await TransitionTaskStateAsync(taskId ?? string.Empty, TaskStatus.Failed);
                break;
            case ExecutionDecision.Pause:
                await TransitionTaskStateAsync(taskId ?? string.Empty, TaskStatus.Paused);
                break;
        }
        return ExecutionDispatcher.BuildDirective(decision, snapshot);
    }

    /// <summary>
    /// Moves the task to the given status through the guarded state machine (e.g. Running →
    /// Paused on the Pause decision, Running → Failed on FailTask). An illegal transition
    /// (already terminal, wrong phase) is a logged no-op — the directive still tells the loop
    /// to end the turn, but the task is never moved to a state the machine forbids.
    /// </summary>
    private async Task TransitionTaskStateAsync(string taskId, TaskStatus to)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        var task = await _taskManager.GetTaskAsync(taskId);
        if (task == null) return;
        var next = TaskStateMachine.TryTransition(task, to);
        if (next == null)
        {
            _logger?.LogDebug("Transition {From} → {To} is not legal for task {TaskId}; no-op.", task.Status, to, taskId);
            return;
        }
        await _taskManager.SaveTaskAsync(next);
        _logger?.LogInformation("Task {TaskId} transitioned {From} → {To} by the dispatcher.", taskId, task.Status, to);
    }

    /// <summary>
    /// Runs the deterministic completion gate on a task_complete claim. The model's claim is
    /// merely an input; the gate decides against the checklist AND the evidence (P0): an
    /// empty checklist with missing/stale/failed verification is rejected. Accepted ⇒ the
    /// task is marked Completed (through the state machine), rejected ⇒ the reason is
    /// returned and the loop continues.
    /// </summary>
    public async Task<(bool Accepted, string? Reason)> EvaluateCompletionClaimAsync(
        string taskId,
        IReadOnlyList<ToolExecutor.PlanEntry> plan,
        string? summary)
    {
        var eligibility = BuildCompletionEligibility(taskId, plan);
        var verdict = AgentSupervisor.EvaluateCompletion(
            plan.Where(e => !e.Done).Select(e => e.Text).ToList(), eligibility);
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
    /// P1.8: the supervisor's decision from a TaskExecutionSnapshot — the ONLY input the
    /// supervisor needs. The live loop builds the snapshot (task/run/step/plan/queue/outcome/
    /// state delta) and hands it over; the decision is pure and deterministic.
    /// </summary>
    public Task<SupervisorDecision> DecideAfterTurnAsync(
        TaskExecutionSnapshot snapshot,
        int maxCompletionRejections = 3,
        int maxStalledTurns = 6)
        => Task.FromResult(AgentSupervisor.DecideAfterTurn(snapshot, maxCompletionRejections, maxStalledTurns));

}
