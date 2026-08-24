namespace Klydis.Core.Chat;

/// <summary>
/// Machine-readable reason why a model turn ended. A bare text response
/// (TextResponseOnly) should NOT automatically terminate an autonomous run
/// if unfinished work exists — the supervisor evaluates continuation.
/// </summary>
public enum TurnTerminationReason
{
    /// <summary>Model produced a tool call — execution continues.</summary>
    ToolCall,
    /// <summary>Model created or updated the execution plan.</summary>
    PlanUpdate,
    /// <summary>Model created or updated a TODO item.</summary>
    TodoUpdate,
    /// <summary>Model called task_complete.</summary>
    TaskComplete,
    /// <summary>All objective completion criteria verified by harness.</summary>
    ObjectiveComplete,
    /// <summary>Model explicitly requested user input.</summary>
    NeedsUserInput,
    /// <summary>Execution cannot proceed without intervention.</summary>
    Blocked,
    /// <summary>An unrecoverable error occurred.</summary>
    Error,
    /// <summary>Turn/token/time budget exhausted.</summary>
    BudgetExhausted,
    /// <summary>Model produced only a text response with no tool call.
    /// This should NOT terminate an autonomous run if unfinished work exists.</summary>
    TextResponseOnly,
    /// <summary>Generation was truncated by context limit.</summary>
    GenerationTruncated,
    /// <summary>Loop or stagnation detected.</summary>
    StagnationDetected
}
