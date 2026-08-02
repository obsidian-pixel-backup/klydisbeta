using System.Collections.Generic;

namespace Klydis.Core.Chat;

/// <summary>
/// Event types emitted during autonomous goal loop orchestration.
/// </summary>
public enum GoalStreamEventType
{
    /// <summary>
    /// Event forwarded from the inner ChatEngine stream (Token, ToolCall, ToolResult, etc.)
    /// </summary>
    InnerEvent,

    /// <summary>
    /// Outer turn loop started.
    /// </summary>
    TurnStarted,

    /// <summary>
    /// Outer turn loop completed.
    /// </summary>
    TurnCompleted,

    /// <summary>
    /// Goal progress percentage updated (via task_progress tool or turn calculation).
    /// </summary>
    ProgressUpdated,

    /// <summary>
    /// Model signaled goal completion via task_complete tool.
    /// </summary>
    GoalComplete,

    /// <summary>
    /// Goal loop terminated due to budget exhaustion or timeout.
    /// </summary>
    BudgetExhausted
}

/// <summary>
/// Event emitted by GoalOrchestrator to report goal progress and stream tokens/tools.
/// </summary>
public record GoalStreamEvent(GoalStreamEventType Type, string Content, IDictionary<string, object>? Metadata = null)
{
    /// <summary>
    /// The wrapped inner ChatStreamEvent if Type is InnerEvent.
    /// </summary>
    public ChatStreamEvent? InnerEvent { get; init; }

    public static GoalStreamEvent FromInnerEvent(ChatStreamEvent inner) =>
        new GoalStreamEvent(GoalStreamEventType.InnerEvent, inner.Content, inner.Metadata) { InnerEvent = inner };

    public static GoalStreamEvent TurnStarted(int turnNumber) =>
        new GoalStreamEvent(GoalStreamEventType.TurnStarted, $"Turn {turnNumber} started", new Dictionary<string, object> { ["TurnNumber"] = turnNumber });

    public static GoalStreamEvent TurnCompleted(int turnNumber, GoalExecutionState state) =>
        new GoalStreamEvent(GoalStreamEventType.TurnCompleted, $"Turn {turnNumber} completed", new Dictionary<string, object>
        {
            ["TurnNumber"] = turnNumber,
            ["State"] = state
        });

    public static GoalStreamEvent ProgressUpdated(int percent, int turn, string status = "") =>
        new GoalStreamEvent(GoalStreamEventType.ProgressUpdated, status, new Dictionary<string, object>
        {
            ["Percent"] = percent,
            ["TurnNumber"] = turn
        });

    public static GoalStreamEvent GoalComplete(GoalExecutionState state) =>
        new GoalStreamEvent(GoalStreamEventType.GoalComplete, state.CompletionSummary ?? "Goal completed successfully.", new Dictionary<string, object> { ["State"] = state });

    public static GoalStreamEvent BudgetExhausted(string reason) =>
        new GoalStreamEvent(GoalStreamEventType.BudgetExhausted, reason);
}
