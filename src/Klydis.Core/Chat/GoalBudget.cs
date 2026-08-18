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
    /// Maximum consecutive task_complete claims rejected by the deterministic verifier
    /// before the run halts. Guards against a model that insists it is done while plan
    /// items remain open (the "repeats the same action without progress" failure mode).
    /// Default 3.
    /// </summary>
    public int MaxCompletionRejections { get; set; } = 3;

    /// <summary>
    /// Consecutive tool-calling turns with no completed plan items before a stagnation
    /// warning is emitted and injected into the next turn. Progress is measured by the
    /// checklist, not by generated text. Default 6.
    /// </summary>
    public int MaxStalledTurns { get; set; } = 6;

    /// <summary>
    /// Hard safety ceiling for consecutive non-advancing (empty) turns. Unlike
    /// <see cref="MaxConsecutiveEmptyTurns"/>, this applies EVEN in Infinite Mode —
    /// "unlimited" means no soft completion target, never no safety ceiling.
    /// Default 20.
    /// </summary>
    public int MaxHardConsecutiveEmptyTurns { get; set; } = 20;

    /// <summary>
    /// Hard safety ceiling for rejected completion claims. Applies even in Infinite Mode.
    /// Default 5 (above the soft <see cref="MaxCompletionRejections"/> of 3).
    /// </summary>
    public int MaxHardCompletionRejections { get; set; } = 5;

    /// <summary>
    /// Checks whether current execution state is within budget bounds.
    /// </summary>
    public bool IsWithinLimits(GoalExecutionState state)
    {
        // P1.17: hard circuit breakers always apply. AllowInfinite only lifts the SOFT
        // completion targets (turn/token/wall-time budgets) — it must never disable the
        // breakers that stop pathological loops (endless empty turns, endless rejected
        // completion claims).
        if (state.ConsecutiveEmptyTurns >= MaxHardConsecutiveEmptyTurns) return false;
        if (state.CompletionRejections >= MaxHardCompletionRejections) return false;
        if (AllowInfinite) return true;

        if (state.TurnCount >= MaxTurns) return false;
        if (state.TotalTokensGenerated >= MaxTotalTokens) return false;
        if (state.ElapsedTime >= MaxWallTime) return false;
        if (state.ConsecutiveEmptyTurns >= MaxConsecutiveEmptyTurns) return false;
        if (state.CompletionRejections >= MaxCompletionRejections) return false;

        return true;
    }

    /// <summary>
    /// Gets human-readable reason why budget limit was reached.
    /// </summary>
    public string GetExhaustionReason(GoalExecutionState state)
    {
        if (state.ConsecutiveEmptyTurns >= MaxHardConsecutiveEmptyTurns)
            return $"Agent reached the hard ceiling of {MaxHardConsecutiveEmptyTurns} consecutive non-advancing turns. Execution halted to prevent infinite loop stall (this ceiling applies even in Infinite Mode).";
        if (state.CompletionRejections >= MaxHardCompletionRejections)
            return $"Task completion claim was rejected {MaxHardCompletionRejections} times by the deterministic verifier (hard ceiling, applies even in Infinite Mode). Execution halted because 'done' could not be verified.";
        if (state.ConsecutiveEmptyTurns >= MaxConsecutiveEmptyTurns)
            return $"Agent reached max consecutive non-advancing turns ({MaxConsecutiveEmptyTurns}). Execution paused to prevent infinite loop stall.";
        if (state.CompletionRejections >= MaxCompletionRejections)
            return $"Task completion claim was rejected {MaxCompletionRejections} times by the deterministic verifier (plan items remained open). Execution halted because 'done' could not be verified.";
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
