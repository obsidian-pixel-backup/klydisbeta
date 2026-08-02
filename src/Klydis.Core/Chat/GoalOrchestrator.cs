using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Chat;

/// <summary>
/// Outer orchestration loop that re-invokes ChatEngine across multiple turns
/// until a goal is completed or budget is exhausted.
/// </summary>
public class GoalOrchestrator(
    ChatEngine chatEngine,
    ILogger<GoalOrchestrator>? logger = null)
{
    private readonly ChatEngine _chatEngine = chatEngine ?? throw new ArgumentNullException(nameof(chatEngine));
    private readonly ILogger<GoalOrchestrator>? _logger = logger;

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

            // 2. Build turn prompt (turn 1 uses the original user goal; turn 2+ uses autonomous continuation prompt)
            string turnPrompt = state.TurnCount == 1
                ? userGoal
                : BuildContinuationPrompt(state);

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

                        // Tool call detection
                        if (evt.Type == ChatStreamEventType.ToolCall)
                        {
                            toolCallCountInTurn++;
                            lastToolCalled = evt.Content;

                            if (string.Equals(evt.Content, "task_complete", StringComparison.OrdinalIgnoreCase))
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

            if (goalComplete)
            {
                state.ProgressPercent = 100;
                yield return GoalStreamEvent.ProgressUpdated(100, state.TurnCount, "Goal Completed!");
                yield return GoalStreamEvent.GoalComplete(state);
                _logger?.LogInformation("Goal completed successfully in {TurnCount} turn(s).", state.TurnCount);
                break;
            }

            yield return GoalStreamEvent.TurnCompleted(state.TurnCount, state);

            // Brief delay between turns to allow UI dispatch and prevent CPU hammering
            await Task.Delay(100, ct);
        }
    }

    private static string BuildContinuationPrompt(GoalExecutionState state)
    {
        return $"[SYSTEM — AUTONOMOUS GOAL CONTINUATION]\n" +
               $"Original Goal: \"{state.OriginalGoal}\"\n" +
               $"Current Execution Status: Turn {state.TurnCount} complete. Estimated Progress: {state.ProgressPercent}%\n" +
               $"Budget: {state.Budget.GetRemainingDescription(state)}\n\n" +
               $"Instructions:\n" +
               $"1. Analyze your findings and work completed in previous turns.\n" +
               $"2. If your requested goal is 100% complete, call tool 'task_complete' with argument {{\"summary\": \"<summary>\"}}.\n" +
               $"3. If further work or tool calls are required, proceed immediately with the next step using <tool_call>.\n" +
               $"4. Periodically call tool 'task_progress' to report your completion percentage.\n" +
               $"5. Do NOT give up or ask the user for confirmation — work autonomously until the goal is fully accomplished.";
    }
}
