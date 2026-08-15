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

    /// <summary>
    /// Number of consecutive task_complete claims rejected by the deterministic verifier
    /// (open plan items still existed). Resets to zero only when a claim is accepted.
    /// </summary>
    public int CompletionRejections { get; set; }

    /// <summary>
    /// Human-readable reason from the most recent rejected completion claim, injected into
    /// the next continuation prompt so the model knows exactly which work remains open.
    /// </summary>
    public string? LastVerificationRejection { get; set; }

    /// <summary>
    /// Consecutive turns that executed tool calls while the deterministic progress signal
    /// (completed plan items) did not advance — the silent-failure detector.
    /// </summary>
    public int ConsecutiveStalledTurns { get; set; }

    /// <summary>
    /// Most recent stagnation warning, injected into the next continuation prompt so the
    /// model reassesses instead of repeating a non-advancing pattern.
    /// </summary>
    public string? LastStagnationNotice { get; set; }
}
