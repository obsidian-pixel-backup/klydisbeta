using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// Multi-dimensional budget tracking system for autonomous task runs (P0).
/// Decouples execution accounting into independent orthogonal resource dimensions.
/// </summary>
public sealed class ProgressBudget
{
    // Thresholds
    public int MaxGenerations { get; set; } = 40;
    public int MaxActions { get; set; } = 50;
    public int MaxFailures { get; set; } = 8;
    public int MaxRetries { get; set; } = 5;
    public int MaxRejections { get; set; } = 6;
    public int MaxStagnantCycles { get; set; } = 5;
    public int MaxUnsupportedClaims { get; set; } = 3;
    public TimeSpan MaxWallTime { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan MaxActiveComputeTime { get; set; } = TimeSpan.FromMinutes(10);

    // Current Usage
    public int UsedGenerations { get; private set; } = 0;
    public int UsedActions { get; private set; } = 0;
    public int UsedFailures { get; private set; } = 0;
    public int UsedRetries { get; private set; } = 0;
    public int UsedRejections { get; private set; } = 0;
    public int StagnantCycles { get; private set; } = 0;
    public int UsedUnsupportedClaims { get; private set; } = 0;
    public TimeSpan ElapsedWallTime { get; set; } = TimeSpan.Zero;
    public TimeSpan ElapsedActiveComputeTime { get; set; } = TimeSpan.Zero;

    public void RecordGeneration(bool wasStagnant = false)
    {
        UsedGenerations++;
        if (wasStagnant) StagnantCycles++;
        else StagnantCycles = 0;
    }

    public void RecordAction(bool success)
    {
        UsedActions++;
        if (!success) UsedFailures++;
    }

    public void RecordRetry() => UsedRetries++;
    public void RecordRejection() => UsedRejections++;
    public void RecordUnsupportedClaim() => UsedUnsupportedClaims++;

    /// <summary>
    /// Evaluates all independent budget dimensions.
    /// Returns IsExceeded=true and the specific exhausted dimension if any limit is breached.
    /// </summary>
    public (bool IsExceeded, string? Reason) CheckBudget()
    {
        if (UsedGenerations >= MaxGenerations)
            return (true, $"Generation budget exhausted ({UsedGenerations}/{MaxGenerations} generations)");

        if (UsedActions >= MaxActions)
            return (true, $"Action budget exhausted ({UsedActions}/{MaxActions} tool calls)");

        if (UsedFailures >= MaxFailures)
            return (true, $"Failure budget exhausted ({UsedFailures}/{MaxFailures} failed tool calls)");

        if (UsedRetries >= MaxRetries)
            return (true, $"Retry budget exhausted ({UsedRetries}/{MaxRetries} retries)");

        if (UsedRejections >= MaxRejections)
            return (true, $"Action rejection budget exhausted ({UsedRejections}/{MaxRejections} gate rejections)");

        if (StagnantCycles >= MaxStagnantCycles)
            return (true, $"Stagnation budget exhausted ({StagnantCycles}/{MaxStagnantCycles} consecutive cycles without progress)");

        if (UsedUnsupportedClaims >= MaxUnsupportedClaims)
            return (true, $"Evidence budget exhausted ({UsedUnsupportedClaims}/{MaxUnsupportedClaims} unsupported factual claims)");

        if (ElapsedWallTime > MaxWallTime)
            return (true, $"Wall-clock time budget exhausted ({ElapsedWallTime.TotalMinutes:F1}/{MaxWallTime.TotalMinutes:F1} min)");

        if (ElapsedActiveComputeTime > MaxActiveComputeTime)
            return (true, $"Active compute budget exhausted ({ElapsedActiveComputeTime.TotalMinutes:F1}/{MaxActiveComputeTime.TotalMinutes:F1} min)");

        return (false, null);
    }

    /// <summary>Resets all usage counters for a fresh run.</summary>
    public void Reset()
    {
        UsedGenerations = 0;
        UsedActions = 0;
        UsedFailures = 0;
        UsedRetries = 0;
        UsedRejections = 0;
        StagnantCycles = 0;
        UsedUnsupportedClaims = 0;
        ElapsedWallTime = TimeSpan.Zero;
        ElapsedActiveComputeTime = TimeSpan.Zero;
    }
}
