using System;
using System.Collections.Concurrent;

namespace Klydis.Core.Tasks;

/// <summary>
/// Assessment produced by the state-delta stagnation tracker.
/// </summary>
public sealed record StagnationAssessment(
    bool IsStagnated,
    int ConsecutiveIdleTurns,
    int MaxPermittedIdleTurns,
    string? Reason);

/// <summary>
/// Authoritative state-delta stagnation tracker (Phase 17).
/// Tracks factual progress deltas (files changed, plan updated, step completed, evidence added)
/// rather than raw turn counts to detect real execution stalls.
/// </summary>
public interface IStateDeltaStagnationTracker
{
    /// <summary>Records a completed turn's state delta and evaluates stagnation.</summary>
    StagnationAssessment RecordTurn(string taskId, StateDelta delta);

    /// <summary>Resets the stagnation tracking for a task.</summary>
    void Reset(string taskId);
}

/// <summary>
/// Concrete implementation of <see cref="IStateDeltaStagnationTracker"/>.
/// </summary>
public sealed class StateDeltaStagnationTracker : IStateDeltaStagnationTracker
{
    private readonly int _maxIdleTurns;
    private readonly ConcurrentDictionary<string, int> _idleTurnCounts = new(StringComparer.Ordinal);

    public StateDeltaStagnationTracker(int maxIdleTurns = 3)
    {
        _maxIdleTurns = maxIdleTurns > 0 ? maxIdleTurns : 3;
    }

    /// <inheritdoc />
    public StagnationAssessment RecordTurn(string taskId, StateDelta delta)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return new StagnationAssessment(false, 0, _maxIdleTurns, null);
        }

        bool madeProgress = delta != null && !delta.IsEmpty &&
            (delta.Contains(StateDeltaKind.FileChanged) ||
             delta.Contains(StateDeltaKind.StepCompleted) ||
             delta.Contains(StateDeltaKind.PlanChanged) ||
             delta.Contains(StateDeltaKind.EvidenceAdded) ||
             delta.Contains(StateDeltaKind.ToolSucceeded));

        int idleCount;
        if (madeProgress)
        {
            _idleTurnCounts[taskId] = 0;
            idleCount = 0;
        }
        else
        {
            idleCount = _idleTurnCounts.AddOrUpdate(taskId, 1, (_, cur) => cur + 1);
        }

        bool isStagnated = idleCount >= _maxIdleTurns;
        string? reason = isStagnated
            ? $"Execution stagnated: {idleCount} consecutive turns produced no factual state deltas (files, plan, evidence)."
            : null;

        return new StagnationAssessment(isStagnated, idleCount, _maxIdleTurns, reason);
    }

    /// <inheritdoc />
    public void Reset(string taskId)
    {
        if (!string.IsNullOrEmpty(taskId))
        {
            _idleTurnCounts.TryRemove(taskId, out _);
        }
    }
}
