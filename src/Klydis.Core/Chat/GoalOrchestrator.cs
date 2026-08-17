using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Chat;

/// <summary>
/// Source of deterministic completion evidence for the goal loop's verification gate.
/// "Done" is decided by the harness, not the model's self-assessment.
/// </summary>
public interface IGoalCompletionVerifier
{
    /// <summary>
    /// Open (not-done) plan items, or NULL when the plan state could not be read. Null is
    /// the fail-closed signal: completion verification is UNAVAILABLE, so a completion
    /// claim must be REJECTED (the old empty-list-on-failure behavior accepted claims on
    /// a read fault — a database failure could "complete" a task with open work).
    /// </summary>
    IReadOnlyList<string>? GetOpenPlanItems(string sessionId);

    /// <summary>
    /// Deterministic progress signal for the session's plan: (Total, Completed) item counts.
    /// (0, 0) when there is no plan. Used for stagnation detection — "progress" is defined
    /// as checking off plan items, never as merely generating more text.
    /// </summary>
    (int Total, int Completed) GetPlanProgress(string sessionId);

    /// <summary>
    /// The full plan checklist with done flags — the durable source for the continuation
    /// contract (completed / in-progress / pending / completion criteria).
    /// </summary>
    IReadOnlyList<ToolExecutor.PlanEntry> GetPlanEntries(string sessionId);
}

/// <summary>
/// Deterministic progress/stagnation tracker (essay §33/§34): a turn "advanced" only when a
/// plan item was completed (or the plan was first established). Tool calls that complete no
/// plan items and change no durable state are the silent-failure signal; after repeated such
/// turns the harness injects a reassessment notice instead of letting the loop spin forever.
/// </summary>
public static class GoalStagnationTracker
{
    /// <summary>
    /// Computes the next consecutive-stalled-turn count given plan snapshots before/after a
    /// turn. Stalls only count when a plan exists with open items; a turn with no tool calls,
    /// a plan that advanced, a just-created plan, or a fully-complete plan is never stalled.
    /// </summary>
    public static int ComputeNextStallCount(
        (int Total, int Completed) before,
        (int Total, int Completed) after,
        bool hadToolCalls,
        int currentStalled)
    {
        if (!hadToolCalls) return 0;

        bool planEstablished = before.Total == 0 && after.Total > 0;
        bool planAdvanced = after.Completed > before.Completed;
        if (planEstablished || planAdvanced) return 0;

        // No plan, or plan fully complete (verification/wrap-up phase) => not stalled.
        if (after.Total == 0 || after.Completed >= after.Total) return 0;

        return currentStalled + 1;
    }
}

/// <summary>
/// Verdict of the deterministic completion gate.
/// </summary>
public readonly record struct GoalCompletionVerdict(bool Accepted, string? Reason);

/// <summary>
/// Outer orchestration loop that re-invokes ChatEngine across multiple turns
/// until a goal is completed or budget is exhausted.
/// </summary>
public class GoalOrchestrator(
    ChatEngine chatEngine,
    ILogger<GoalOrchestrator>? logger = null,
    IGoalCompletionVerifier? completionVerifier = null)
{
    private readonly ChatEngine _chatEngine = chatEngine ?? throw new ArgumentNullException(nameof(chatEngine));
    private readonly ILogger<GoalOrchestrator>? _logger = logger;
    private readonly IGoalCompletionVerifier? _completionVerifier = completionVerifier ?? chatEngine as IGoalCompletionVerifier;

    /// <summary>
    /// Deterministic progress snapshot captured before each outer turn; compared against the
    /// post-turn snapshot to detect stagnation (tool calls happening, nothing changing).
    /// </summary>
    private (int Total, int Completed) _planSnapshotBeforeTurn;

    /// <summary>
    /// The deterministic completion gate (the diagram's "Deterministic Verifier"): a
    /// task_complete claim is accepted only when every gating check passes — here, the
    /// session's plan has no open items. The model's opinion that it is done is not
    /// sufficient; real "done" requires the harness to confirm the checklist is empty.
    /// Owned by <see cref="Klydis.Core.Tasks.AgentSupervisor"/>; this delegates so the
    /// legacy orchestrator and the live loop always agree on the verdict.
    /// </summary>
    public static GoalCompletionVerdict EvaluateCompletion(IReadOnlyList<string>? openPlanItems)
        => Klydis.Core.Tasks.AgentSupervisor.EvaluateCompletion(openPlanItems);

    /// <summary>
    /// Executes an autonomous long-horizon goal loop.
    /// Re-invokes ChatEngine turn-by-turn until task_complete is called or budget limits are reached.
    /// </summary>
    public async IAsyncEnumerable<GoalStreamEvent> RunGoalAsync(
        string userGoal,
        GoalBudget? budget = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        string? skillContext = null)
    {
        if (string.IsNullOrWhiteSpace(userGoal))
            yield break;

        budget ??= new GoalBudget();
        var state = new GoalExecutionState(userGoal, budget);
        bool goalComplete = false;

        _logger?.LogInformation("Starting autonomous goal execution for goal: {Goal}", userGoal);

        while (!goalComplete && !ct.IsCancellationRequested)
        {
            state.TurnCount++;
            state.ElapsedTime = DateTime.UtcNow - state.StartTime;

            // 1. Check budget limits
            if (!budget.IsWithinLimits(state))
            {
                string reason = budget.GetExhaustionReason(state);
                _logger?.LogWarning("Goal execution halted due to budget limit: {Reason}", reason);
                yield return GoalStreamEvent.BudgetExhausted(reason);
                break;
            }

            yield return GoalStreamEvent.TurnStarted(state.TurnCount);

            // Deterministic progress signal: snapshot the plan checklist before the turn so
            // stagnation (tool calls with zero plan progress) can be measured afterward.
            _planSnapshotBeforeTurn = _completionVerifier?.GetPlanProgress(_chatEngine.CurrentSessionId) ?? (0, 0);

            // 2. Build turn prompt (turn 1 uses the original user goal; turn 2+ uses autonomous continuation prompt)
            string turnPrompt = state.TurnCount == 1
                ? userGoal
                : BuildContinuationPrompt(state, BuildContract(state));

            int turnTokenCount = 0;
            int toolCallCountInTurn = 0;
            string? lastToolCalled = null;

            // 3. Invoke ChatEngine inner loop
            var enumerator = _chatEngine.StreamResponseAsync(turnPrompt, ct, skillContext).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    ChatStreamEvent? evt = null;
                    Exception? turnException = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync()) break;
                        evt = enumerator.Current;
                    }
                    catch (Exception ex)
                    {
                        turnException = ex;
                    }

                    if (turnException != null)
                    {
                        _logger?.LogError(turnException, "Error in GoalOrchestrator turn {Turn}", state.TurnCount);
                        yield return GoalStreamEvent.FromInnerEvent(new ChatStreamEvent(ChatStreamEventType.Error, turnException.Message));
                        break;
                    }

                    if (evt != null)
                    {
                        yield return GoalStreamEvent.FromInnerEvent(evt);

                        // Token tracking
                        if (evt.Type == ChatStreamEventType.Token && !string.IsNullOrEmpty(evt.Content))
                        {
                            int est = Math.Max(1, evt.Content.Length / 4);
                            turnTokenCount += est;
                            state.TotalTokensGenerated += est;
                        }

                        // H1: Also count ToolResult payloads — tool output tokens are real context pressure
                        if (evt.Type == ChatStreamEventType.ToolResult && !string.IsNullOrEmpty(evt.Content))
                        {
                            int toolEst = Math.Max(1, evt.Content.Length / 4);
                            state.TotalTokensGenerated += toolEst;
                        }

                        // Tool call detection
                        if (evt.Type == ChatStreamEventType.ToolCall)
                        {
                            toolCallCountInTurn++;
                            lastToolCalled = evt.Content;

                            if (string.Equals(evt.Content, "task_complete", StringComparison.OrdinalIgnoreCase))
                            {
                                // Deterministic verification gate: "done" only when every gating
                                // check exits 0. The persisted plan is the authoritative checklist;
                                // a claim of completion while items remain open is rejected and the
                                // run continues (bounded by MaxCompletionRejections). A NULL result
                                // means the plan could not be read — verification is UNAVAILABLE,
                                // which must also reject the claim (fail closed, never accept on a
                                // read failure).
                                var openItems = _completionVerifier?.GetOpenPlanItems(_chatEngine.CurrentSessionId);
                                var verdict = EvaluateCompletion(openItems);

                                if (verdict.Accepted)
                                {
                                    goalComplete = true;
                                    if (evt.Metadata != null && evt.Metadata.TryGetValue("Arguments", out var rawArgs) && rawArgs is IDictionary<string, object> argsDict)
                                    {
                                        if (argsDict.TryGetValue("summary", out var summaryObj))
                                        {
                                            state.CompletionSummary = ToolExecutor.UnwrapJsonElement(summaryObj)?.ToString();
                                        }
                                    }
                                }
                                else
                                {
                                    state.CompletionRejections++;
                                    state.LastVerificationRejection = verdict.Reason;
                                    _logger?.LogWarning(
                                        "Deterministic verifier rejected task_complete claim (rejection {Rejection}): {Reason}",
                                        state.CompletionRejections, verdict.Reason);
                                    yield return GoalStreamEvent.VerificationFailed(verdict.Reason ?? "Completion claim rejected", state.CompletionRejections, openItems);
                                }
                            }
                            else if (string.Equals(evt.Content, "task_progress", StringComparison.OrdinalIgnoreCase))
                            {
                                if (evt.Metadata != null && evt.Metadata.TryGetValue("Arguments", out var rawArgs) && rawArgs is IDictionary<string, object> argsDict)
                                {
                                    if (argsDict.TryGetValue("percent", out var pctObj))
                                    {
                                        var rawVal = ToolExecutor.UnwrapJsonElement(pctObj)?.ToString();
                                        if (int.TryParse(rawVal, out int pct))
                                        {
                                            state.ProgressPercent = Math.Clamp(pct, 0, 100);
                                        }
                                    }
                                    string statusMsg = "";
                                    if (argsDict.TryGetValue("status", out var statusObj))
                                    {
                                        statusMsg = ToolExecutor.UnwrapJsonElement(statusObj)?.ToString() ?? "";
                                    }
                                    yield return GoalStreamEvent.ProgressUpdated(state.ProgressPercent, state.TurnCount, statusMsg);
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            state.ElapsedTime = DateTime.UtcNow - state.StartTime;

            // Track non-advancing / empty turns
            if (toolCallCountInTurn == 0 && turnTokenCount < 10)
            {
                state.ConsecutiveEmptyTurns++;
            }
            else
            {
                state.ConsecutiveEmptyTurns = 0;
            }

            state.TurnSummaries.Add($"Turn {state.TurnCount}: {toolCallCountInTurn} tool call(s), last: {lastToolCalled ?? "none"}, ~{turnTokenCount} tokens.");

            // Stagnation detection: consecutive tool turns that complete no plan items are the
            // "silent failure" signal — the loop keeps acting while durable state never moves.
            var planAfter = _completionVerifier?.GetPlanProgress(_chatEngine.CurrentSessionId) ?? (0, 0);
            state.ConsecutiveStalledTurns = GoalStagnationTracker.ComputeNextStallCount(
                _planSnapshotBeforeTurn, planAfter, toolCallCountInTurn > 0, state.ConsecutiveStalledTurns);

            // Continuation supervisor: after EVERY turn (not just on task_complete claims),
            // the harness checks durable state and issues the authoritative verdict. A model
            // that stops generating while plan items are open or messages are queued keeps the
            // task ACTIVE — model termination has zero authority over task completion.
            var contract = BuildContract(state);
            var supervisorVerdict = GoalSupervisor.EvaluateContinuation(
                goalComplete,
                (contract.CompletionCriteria.Count, contract.Completed.Count),
                contract.PendingQueueItems);
            yield return GoalStreamEvent.SupervisorVerdict(supervisorVerdict);

            if (state.ConsecutiveStalledTurns >= state.Budget.MaxStalledTurns)
            {
                var openItems = _completionVerifier?.GetOpenPlanItems(_chatEngine.CurrentSessionId) ?? Array.Empty<string>();
                state.LastStagnationNotice = openItems.Count > 0
                    ? $"You have executed {state.ConsecutiveStalledTurns} consecutive turns with tool calls but checked off NO plan items. The harness measures progress by your checklist, not by text. Reassess: pick the next open item ({string.Join("; ", openItems.Take(3))}{(openItems.Count > 3 ? " …" : "")}) and either complete it now, or revise the plan if it no longer reflects reality."
                    : $"You have executed {state.ConsecutiveStalledTurns} consecutive turns with tool calls but the plan did not advance. Reassess your approach or revise the plan.";
                _logger?.LogWarning("Stagnation detected at turn {Turn}: {Notice}", state.TurnCount, state.LastStagnationNotice);
                yield return GoalStreamEvent.StagnationDetected(state.LastStagnationNotice, state.ConsecutiveStalledTurns);
                // Reset so the notice is injected once per window rather than spammed every turn.
                state.ConsecutiveStalledTurns = 0;
            }

            if (goalComplete)
            {
                state.ProgressPercent = 100;
                yield return GoalStreamEvent.ProgressUpdated(100, state.TurnCount, "Goal Completed!");
                yield return GoalStreamEvent.GoalComplete(state);
                _logger?.LogInformation("Goal completed successfully in {TurnCount} turn(s).", state.TurnCount);
                break;
            }

            // Loop guard: a model that keeps claiming completion while plan items stay open is
            // the "same action without progress" failure mode — halt instead of cycling forever.
            if (state.CompletionRejections >= state.Budget.MaxCompletionRejections)
            {
                string haltReason = $"Autonomous goal halted: task_complete was claimed and rejected {state.CompletionRejections} consecutive times by the deterministic verifier. The goal is NOT verified complete; the following plan items remain open: " +
                                    string.Join("; ", (_completionVerifier?.GetOpenPlanItems(_chatEngine.CurrentSessionId) ?? Array.Empty<string>()).Take(5));
                _logger?.LogWarning("Goal halted after {Count} rejected completion claims.", state.CompletionRejections);
                yield return GoalStreamEvent.VerificationFailed(haltReason, state.CompletionRejections);
                break;
            }

            yield return GoalStreamEvent.TurnCompleted(state.TurnCount, state);

            // M6: Raised inter-turn delay to give background consolidation time to settle
            // and provide genuine backpressure so IO/memory tasks from the prior turn complete.
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Assembles the continuation contract from durable sources: the persisted plan checklist
    /// and the message queue. This is execution state, not a conversation summary — a compacted
    /// window can lose prose but never the contract.
    /// </summary>
    private ExecutionStateContract BuildContract(GoalExecutionState state)
    {
        var planEntries = _completionVerifier?.GetPlanEntries(_chatEngine.CurrentSessionId)
            ?? Array.Empty<ToolExecutor.PlanEntry>();
        // Task-scoped (P0.7): the contract counts only the current task's queued messages.
        int pendingQueue = _chatEngine.MessageQueue?.GetPending(_chatEngine.CurrentSessionId, _chatEngine.CurrentTaskId).Count ?? 0;
        return ContinuationContractBuilder.Build(state.OriginalGoal, planEntries, pendingQueue);
    }

    private static string BuildContinuationPrompt(GoalExecutionState state, ExecutionStateContract contract)
    {
        // C5: Inject the last 5 TurnSummaries so the model knows exactly what it has done.
        // Without this, every continuation turn starts with no memory of prior tool calls.
        var historySection = string.Empty;
        if (state.TurnSummaries.Count > 0)
        {
            var recent = state.TurnSummaries.TakeLast(5);
            historySection = $"\nExecution History (last {recent.Count()} turns):\n" +
                             string.Join("\n", recent.Select(s => $"  {s}")) + "\n";
        }

        var stagnationNote = string.Empty;
        if (!string.IsNullOrWhiteSpace(state.LastStagnationNotice))
        {
            stagnationNote = "\n[SYSTEM — STAGNATION WARNING]\n" +
                             state.LastStagnationNotice + "\n";
        }

        var rejectionNote = string.Empty;
        if (!string.IsNullOrWhiteSpace(state.LastVerificationRejection))
        {
            rejectionNote = "\n[SYSTEM — COMPLETION CLAIM REJECTED BY DETERMINISTIC VERIFIER]\n" +
                            $"Your previous task_complete call was NOT accepted as completion. The harness verified the plan checklist and found open work:\n" +
                            $"  {state.LastVerificationRejection}\n" +
                            "Real 'done' requires every plan item to be checked off. Finish the open item(s) above, verify your work, then call task_complete again only when the checklist is genuinely empty.\n";
        }

        return $"[SYSTEM — AUTONOMOUS GOAL CONTINUATION]\n" +
               $"Original Goal: \"{state.OriginalGoal}\"\n" +
               $"Current Execution Status: Turn {state.TurnCount} complete. Estimated Progress: {state.ProgressPercent}%\n" +
               $"Budget: {state.Budget.GetRemainingDescription(state)}\n" +
               ContinuationContractBuilder.Format(contract) +
               "\n" +
               historySection +
               stagnationNote +
               rejectionNote +
               $"\nInstructions:\n" +
               $"1. Review the execution state above — it is authoritative. Do not redo completed items.\n" +
               $"2. If your requested goal is 100% complete AND every completion criterion is checked off, call tool 'task_complete' with argument {{\"summary\": \"<summary>\"}}.\n" +
               $"3. If further work or tool calls are required, proceed immediately with the next step using <tool_call>.\n" +
               $"4. Progress is tracked automatically by the harness from your plan checklist; do not report it yourself.\n" +
               $"5. Do NOT give up or ask the user for confirmation — work autonomously until the goal is fully accomplished.";
    }
}
