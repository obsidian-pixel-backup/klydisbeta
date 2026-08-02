using System;
using System.Collections.Generic;

namespace Klydis.Core.Chat;

/// <summary>
/// Tracks state, progress, telemetry, and history across multiple outer turns during autonomous goal execution.
/// </summary>
public class GoalExecutionState
{
    public GoalExecutionState(string originalGoal, GoalBudget? budget = null)
    {
        OriginalGoal = originalGoal ?? throw new ArgumentNullException(nameof(originalGoal));
        Budget = budget ?? new GoalBudget();
        StartTime = DateTime.UtcNow;
    }

    /// <summary>
    /// The user's original goal prompt.
    /// </summary>
    public string OriginalGoal { get; }

    /// <summary>
    /// Active goal budget parameters.
    /// </summary>
    public GoalBudget Budget { get; set; }

    /// <summary>
    /// Current outer turn iteration count (1-based).
    /// </summary>
    public int TurnCount { get; set; }

    /// <summary>
    /// Estimated total generated tokens across all turns.
    /// </summary>
    public int TotalTokensGenerated { get; set; }

    /// <summary>
    /// Estimated progress percentage (0-100).
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Timestamp when goal execution started (UTC).
    /// </summary>
    public DateTime StartTime { get; }

    /// <summary>
    /// Total elapsed time since goal execution started.
    /// </summary>
    public TimeSpan ElapsedTime { get; set; }

    /// <summary>
    /// Number of consecutive turns that yielded no new tool actions or content.
    /// </summary>
    public int ConsecutiveEmptyTurns { get; set; }

    /// <summary>
    /// Brief log or status updates recorded per turn.
    /// </summary>
    public List<string> TurnSummaries { get; } = new();

    /// <summary>
    /// Summary reported by model upon calling task_complete.
    /// </summary>
    public string? CompletionSummary { get; set; }
}
