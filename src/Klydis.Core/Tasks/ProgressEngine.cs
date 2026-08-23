using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// Represents the semantic delta of state advancement achieved in an execution cycle.
/// </summary>
public sealed record ProgressDelta(
    int CompletedSteps,
    int NewEvidence,
    int StateChanges,
    int ArtifactsCreated,
    int VerificationChanges,
    int ErrorsResolved,
    int FailedActions,
    int NoActionNarrations,
    int HallucinatedTools,
    int UnsupportedClaims)
{
    /// <summary>
    /// Computes the net semantic progress score for this delta.
    /// Positive values represent genuine task advancement; negative values represent thrashing/regression.
    /// </summary>
    public double ComputeScore()
    {
        double score = 0.0;
        score += CompletedSteps * 1.0;
        score += NewEvidence * 0.5;
        score += VerificationChanges * 0.3;
        score += ArtifactsCreated * 0.2;
        score += ErrorsResolved * 0.2;
        score += StateChanges * 0.1;

        score -= FailedActions * 0.5;
        score -= NoActionNarrations * 0.5;
        score -= HallucinatedTools * 1.0;
        score -= UnsupportedClaims * 1.0;

        return Math.Round(score, 2);
    }

    /// <summary>True if any positive progress was made in this cycle.</summary>
    public bool HasPositiveProgress => ComputeScore() > 0.0;

    /// <summary>Empty baseline delta.</summary>
    public static ProgressDelta Empty => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Progress Engine (P0) that evaluates semantic task progression across turns and generations.
/// Replaces coarse turn counting with state-delta progress evaluation.
/// </summary>
public sealed class ProgressEngine
{
    private readonly List<double> _history = new();

    /// <summary>Recent progress scores for this run.</summary>
    public IReadOnlyList<double> History => _history;

    /// <summary>Total accumulated progress score.</summary>
    public double TotalScore => _history.Sum();

    /// <summary>Consecutive cycles with non-positive progress (stagnation counter).</summary>
    public int ConsecutiveStagnantCycles { get; private set; } = 0;

    /// <summary>
    /// Evaluates the progress delta between execution snapshots and records the score.
    /// </summary>
    public double RecordCycle(ProgressDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        double score = delta.ComputeScore();
        _history.Add(score);

        if (score <= 0.0)
        {
            ConsecutiveStagnantCycles++;
        }
        else
        {
            ConsecutiveStagnantCycles = 0;
        }

        return score;
    }

    /// <summary>
    /// Evaluates if the agent is in a stagnant loop (e.g. 3 or more cycles without positive progress).
    /// </summary>
    public bool IsStagnant(int threshold = 3) => ConsecutiveStagnantCycles >= threshold;

    /// <summary>Resets the progress history and stagnation counters.</summary>
    public void Reset()
    {
        _history.Clear();
        ConsecutiveStagnantCycles = 0;
    }
}
