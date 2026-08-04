using System;

namespace Klydis.Core.Chat;

/// <summary>
/// Configurable limits and budgets for autonomous goal execution loops.
/// </summary>
public class GoalBudget
{
    /// <summary>
    /// Maximum outer turn iterations. Default 100.
    /// </summary>
    public int MaxTurns { get; set; } = 100;

    /// <summary>
    /// Maximum estimated total tokens across all turns. Default 500,000.
    /// </summary>
    public int MaxTotalTokens { get; set; } = 500_000;

    /// <summary>
    /// Maximum wall-clock time for goal execution. Default 2 hours.
    /// </summary>
    public TimeSpan MaxWallTime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Maximum consecutive empty or non-advancing turns before treating as stuck. Default 5.
    /// Raised from 3 — reasoning-heavy turns with no tool calls are still genuine work.
    /// </summary>
    public int MaxConsecutiveEmptyTurns { get; set; } = 5;

    /// <summary>
    /// When set to true, budget limit checks are bypassed for infinite operation. Default false.
    /// </summary>
    public bool AllowInfinite { get; set; } = false;

    /// <summary>
    /// Checks whether current execution state is within budget bounds.
    /// </summary>
    public bool IsWithinLimits(GoalExecutionState state)
    {
        if (AllowInfinite) return true;

        if (state.TurnCount >= MaxTurns) return false;
        if (state.TotalTokensGenerated >= MaxTotalTokens) return false;
        if (state.ElapsedTime >= MaxWallTime) return false;
        if (state.ConsecutiveEmptyTurns >= MaxConsecutiveEmptyTurns) return false;

        return true;
    }

    /// <summary>
    /// Gets human-readable reason why budget limit was reached.
    /// </summary>
    public string GetExhaustionReason(GoalExecutionState state)
    {
        if (state.ConsecutiveEmptyTurns >= MaxConsecutiveEmptyTurns)
            return $"Agent reached max consecutive non-advancing turns ({MaxConsecutiveEmptyTurns}). Execution paused to prevent infinite loop stall.";
        if (state.TurnCount >= MaxTurns)
            return $"Reached maximum turn limit ({MaxTurns} turns).";
        if (state.TotalTokensGenerated >= MaxTotalTokens)
            return $"Reached token generation budget ({MaxTotalTokens:N0} tokens).";
        if (state.ElapsedTime >= MaxWallTime)
            return $"Reached maximum wall-clock time limit ({MaxWallTime.TotalMinutes:N0} minutes).";

        return "Budget limit reached.";
    }

    /// <summary>
    /// Gets human-readable string summarizing remaining budget.
    /// </summary>
    public string GetRemainingDescription(GoalExecutionState state)
    {
        if (AllowInfinite) return "Infinite Mode (No budget caps)";

        int turnsRemaining = Math.Max(0, MaxTurns - state.TurnCount);
        int tokensRemaining = Math.Max(0, MaxTotalTokens - state.TotalTokensGenerated);
        var timeRemaining = MaxWallTime > state.ElapsedTime ? MaxWallTime - state.ElapsedTime : TimeSpan.Zero;

        return $"{turnsRemaining} turns remaining, ~{tokensRemaining:N0} tokens left, {(int)timeRemaining.TotalMinutes}m remaining";
    }
}
