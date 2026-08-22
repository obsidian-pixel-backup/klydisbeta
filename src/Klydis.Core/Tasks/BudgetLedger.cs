using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// Health and pressure status of the budget.
/// </summary>
public enum BudgetHealthStatus
{
    Healthy,
    Warning80Percent,
    PrioritizeCompletion90Percent,
    GracefulWrapUp95Percent,
    Exhausted
}

/// <summary>
/// Configuration for the goal-level budget.
/// </summary>
public sealed record GoalBudgetConfig
{
    public int MaxTurns { get; init; } = 500;
    public int MaxGenerations { get; init; } = 1000;
    public int MaxToolCalls { get; init; } = 2000;
    public TimeSpan MaxWallTime { get; init; } = TimeSpan.FromMinutes(120);
    public long MaxTokens { get; init; } = 10_000_000;
    public bool AllowInfinite { get; init; } = false;

    public static GoalBudgetConfig Default => new();
    public static GoalBudgetConfig Infinite => new()
    {
        AllowInfinite = true,
        MaxTurns = int.MaxValue,
        MaxGenerations = int.MaxValue,
        MaxToolCalls = int.MaxValue,
        MaxWallTime = TimeSpan.MaxValue,
        MaxTokens = long.MaxValue
    };
}

/// <summary>
/// Configuration for an individual turn budget.
/// </summary>
public sealed record TurnBudgetConfig
{
    public int MaxGenerations { get; init; } = 5;
    public int MaxToolCalls { get; init; } = 25;
    public TimeSpan MaxWallTime { get; init; } = TimeSpan.FromSeconds(180);
    public int MaxOutputTokens { get; init; } = 8192;

    public static TurnBudgetConfig Default => new();
}

/// <summary>
/// Immutable snapshot of the current budget expenditure and remaining limits.
/// </summary>
public sealed record BudgetSnapshot
{
    public required string GoalId { get; init; }
    public required GoalBudgetConfig Config { get; init; }
    public int TurnsCount { get; init; }
    public int GenerationsCount { get; init; }
    public int ToolCallsCount { get; init; }
    public long TotalTokensUsed { get; init; }
    public TimeSpan ElapsedWallTime { get; init; }
    public double MaxPressureRatio { get; init; }
    public BudgetHealthStatus HealthStatus { get; init; }
    public string? GuidanceMessage { get; init; }

    public bool IsExhausted => HealthStatus == BudgetHealthStatus.Exhausted;
}

/// <summary>
/// Budget expenditure events recorded authoritatively.
/// </summary>
public abstract record BudgetEvent(string GoalId, DateTimeOffset Timestamp);
public sealed record GoalStartedEvent(string GoalId, GoalBudgetConfig Config, DateTimeOffset Timestamp) : BudgetEvent(GoalId, Timestamp);
public sealed record TurnStartedEvent(string GoalId, string TurnId, DateTimeOffset Timestamp) : BudgetEvent(GoalId, Timestamp);
public sealed record GenerationCompletedEvent(string GoalId, string TurnId, int InputTokens, int OutputTokens, DateTimeOffset Timestamp) : BudgetEvent(GoalId, Timestamp);
public sealed record ToolCallStartedEvent(string GoalId, string TurnId, string ToolName, DateTimeOffset Timestamp) : BudgetEvent(GoalId, Timestamp);
public sealed record ToolCallCompletedEvent(string GoalId, string TurnId, string ToolName, long DurationMs, DateTimeOffset Timestamp) : BudgetEvent(GoalId, Timestamp);
public sealed record TurnCompletedEvent(string GoalId, string TurnId, DateTimeOffset Timestamp) : BudgetEvent(GoalId, Timestamp);

/// <summary>
/// Authoritative event-based ledger for tracking and enforcing multi-level budgets.
/// </summary>
public interface IBudgetLedger
{
    BudgetSnapshot GetSnapshot(string goalId);
    void RecordGoalStarted(string goalId, GoalBudgetConfig? config = null);
    void RecordTurnStarted(string goalId, string turnId);
    void RecordGeneration(string goalId, string turnId, int inputTokens, int outputTokens);
    void RecordToolCall(string goalId, string turnId, string toolName);
    void RecordToolDuration(string goalId, string turnId, string toolName, long durationMs);
    void RecordTurnCompleted(string goalId, string turnId);
    void AssertCanContinue(string goalId);
}

/// <summary>
/// In-memory and durable-ready implementation of the event-derived Budget Ledger.
/// </summary>
public class BudgetLedger : IBudgetLedger
{
    private readonly ConcurrentDictionary<string, List<BudgetEvent>> _eventsByGoal = new();
    private readonly ConcurrentDictionary<string, GoalBudgetConfig> _configByGoal = new();
    private readonly object _lock = new();

    public void RecordGoalStarted(string goalId, GoalBudgetConfig? config = null)
    {
        var cfg = config ?? GoalBudgetConfig.Default;
        _configByGoal[goalId] = cfg;
        AddEvent(goalId, new GoalStartedEvent(goalId, cfg, DateTimeOffset.UtcNow));
    }

    public void RecordTurnStarted(string goalId, string turnId)
    {
        AddEvent(goalId, new TurnStartedEvent(goalId, turnId, DateTimeOffset.UtcNow));
    }

    public void RecordGeneration(string goalId, string turnId, int inputTokens, int outputTokens)
    {
        AddEvent(goalId, new GenerationCompletedEvent(goalId, turnId, inputTokens, outputTokens, DateTimeOffset.UtcNow));
    }

    public void RecordToolCall(string goalId, string turnId, string toolName)
    {
        AddEvent(goalId, new ToolCallStartedEvent(goalId, turnId, toolName, DateTimeOffset.UtcNow));
    }

    public void RecordToolDuration(string goalId, string turnId, string toolName, long durationMs)
    {
        AddEvent(goalId, new ToolCallCompletedEvent(goalId, turnId, toolName, durationMs, DateTimeOffset.UtcNow));
    }

    public void RecordTurnCompleted(string goalId, string turnId)
    {
        AddEvent(goalId, new TurnCompletedEvent(goalId, turnId, DateTimeOffset.UtcNow));
    }

    private void AddEvent(string goalId, BudgetEvent evt)
    {
        var list = _eventsByGoal.GetOrAdd(goalId, _ => new List<BudgetEvent>());
        lock (list)
        {
            list.Add(evt);
        }
    }

    public BudgetSnapshot GetSnapshot(string goalId)
    {
        var config = _configByGoal.TryGetValue(goalId, out var cfg) ? cfg : GoalBudgetConfig.Default;
        if (!_eventsByGoal.TryGetValue(goalId, out var events))
        {
            return new BudgetSnapshot
            {
                GoalId = goalId,
                Config = config,
                TurnsCount = 0,
                GenerationsCount = 0,
                ToolCallsCount = 0,
                TotalTokensUsed = 0,
                ElapsedWallTime = TimeSpan.Zero,
                MaxPressureRatio = 0,
                HealthStatus = BudgetHealthStatus.Healthy,
                GuidanceMessage = null
            };
        }

        List<BudgetEvent> copy;
        lock (events)
        {
            copy = events.ToList();
        }

        int turns = 0;
        int gens = 0;
        int toolCalls = 0;
        long totalTokens = 0;
        DateTimeOffset? startTime = null;
        DateTimeOffset latestTime = DateTimeOffset.UtcNow;

        foreach (var ev in copy)
        {
            if (startTime == null || ev.Timestamp < startTime)
            {
                startTime = ev.Timestamp;
            }
            if (ev.Timestamp > latestTime)
            {
                latestTime = ev.Timestamp;
            }

            switch (ev)
            {
                case TurnStartedEvent:
                    turns++;
                    break;
                case GenerationCompletedEvent g:
                    gens++;
                    totalTokens += (g.InputTokens + g.OutputTokens);
                    break;
                case ToolCallStartedEvent:
                    toolCalls++;
                    break;
            }
        }

        var elapsed = startTime.HasValue ? (latestTime - startTime.Value) : TimeSpan.Zero;

        if (config.AllowInfinite)
        {
            return new BudgetSnapshot
            {
                GoalId = goalId,
                Config = config,
                TurnsCount = turns,
                GenerationsCount = gens,
                ToolCallsCount = toolCalls,
                TotalTokensUsed = totalTokens,
                ElapsedWallTime = elapsed,
                MaxPressureRatio = 0,
                HealthStatus = BudgetHealthStatus.Healthy,
                GuidanceMessage = "Infinite operation mode."
            };
        }

        double turnRatio = config.MaxTurns > 0 ? (double)turns / config.MaxTurns : 0;
        double genRatio = config.MaxGenerations > 0 ? (double)gens / config.MaxGenerations : 0;
        double toolRatio = config.MaxToolCalls > 0 ? (double)toolCalls / config.MaxToolCalls : 0;
        double tokenRatio = config.MaxTokens > 0 ? (double)totalTokens / config.MaxTokens : 0;
        double timeRatio = config.MaxWallTime.TotalSeconds > 0 ? elapsed.TotalSeconds / config.MaxWallTime.TotalSeconds : 0;

        double maxRatio = Math.Max(turnRatio, Math.Max(genRatio, Math.Max(toolRatio, Math.Max(tokenRatio, timeRatio))));

        BudgetHealthStatus status;
        string? guidance = null;

        if (maxRatio >= 1.0)
        {
            status = BudgetHealthStatus.Exhausted;
            guidance = "HARD BUDGET LIMIT EXHAUSTED. Goal execution must stop.";
        }
        else if (maxRatio >= 0.95)
        {
            status = BudgetHealthStatus.GracefulWrapUp95Percent;
            guidance = "BUDGET AT 95%. Attempt immediate graceful finalization and verify deliverables.";
        }
        else if (maxRatio >= 0.90)
        {
            status = BudgetHealthStatus.PrioritizeCompletion90Percent;
            guidance = "BUDGET AT 90%. Prioritize remaining critical steps; avoid exploratory work.";
        }
        else if (maxRatio >= 0.80)
        {
            status = BudgetHealthStatus.Warning80Percent;
            guidance = "BUDGET WARNING: 80% of allotted resources consumed. Plan actions economically.";
        }
        else
        {
            status = BudgetHealthStatus.Healthy;
        }

        return new BudgetSnapshot
        {
            GoalId = goalId,
            Config = config,
            TurnsCount = turns,
            GenerationsCount = gens,
            ToolCallsCount = toolCalls,
            TotalTokensUsed = totalTokens,
            ElapsedWallTime = elapsed,
            MaxPressureRatio = maxRatio,
            HealthStatus = status,
            GuidanceMessage = guidance
        };
    }

    public void AssertCanContinue(string goalId)
    {
        var snapshot = GetSnapshot(goalId);
        if (snapshot.IsExhausted)
        {
            throw new InvalidOperationException($"Budget for goal '{goalId}' is exhausted ({snapshot.GuidanceMessage}).");
        }
    }
}
