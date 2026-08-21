using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Tasks;

namespace Klydis.Core.Inference;

/// <summary>
/// Detailed diagnostic record for a single model generation turn.
/// </summary>
public sealed record GenerationMetrics(
    string GenerationId,
    string? TaskId,
    string? RunId,
    string? StepId,
    int AttemptNumber,
    int InputTokens,
    int OutputTokens,
    int ThinkingTokens,
    TimeSpan Duration,
    string FinishReason,
    int ToolCallCount,
    StateDelta Delta,
    DateTime TimestampUtc)
{
    /// <summary>Calculated generation throughput in tokens per second.</summary>
    public double TokensPerSecond => Duration.TotalSeconds > 0 ? OutputTokens / Duration.TotalSeconds : 0.0;

    /// <summary>True when reasoning tokens consume more than 85% of generation while producing zero state advancement.</summary>
    public bool IsReasoningOverrun => (OutputTokens + ThinkingTokens > 0) &&
                                      ((double)ThinkingTokens / (OutputTokens + ThinkingTokens) > 0.85) &&
                                      Delta.IsEmpty && ToolCallCount == 0;

    /// <summary>Calculated progress efficiency (state changes per 1,000 tokens).</summary>
    public double ProgressScore
    {
        get
        {
            int total = OutputTokens + ThinkingTokens;
            if (total == 0) return 0.0;
            int mutations = (Delta.PlanChanged ? 2 : 0) + (Delta.EvidenceAdded ? 3 : 0) + (Delta.WorkspaceModified ? 2 : 0);
            return (mutations * 1000.0) / total;
        }
    }
}

/// <summary>
/// Monitors generation efficiency, detects reasoning overruns, and collects diagnostic telemetry.
/// </summary>
public sealed class GenerationWatchdog
{
    private readonly ConcurrentBag<GenerationMetrics> _metrics = new();

    /// <summary>
    /// Records a completed generation's metrics.
    /// </summary>
    public GenerationMetrics RecordGeneration(
        string? taskId,
        string? runId,
        string? stepId,
        int attemptNumber,
        int inputTokens,
        int outputTokens,
        int thinkingTokens,
        TimeSpan duration,
        string finishReason,
        int toolCallCount,
        StateDelta delta)
    {
        string genId = "G-" + Guid.NewGuid().ToString("N")[..8];
        var metric = new GenerationMetrics(
            GenerationId: genId,
            TaskId: taskId,
            RunId: runId,
            StepId: stepId,
            AttemptNumber: attemptNumber,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            ThinkingTokens: thinkingTokens,
            Duration: duration,
            FinishReason: finishReason,
            ToolCallCount: toolCallCount,
            Delta: delta,
            TimestampUtc: DateTime.UtcNow);

        _metrics.Add(metric);
        return metric;
    }

    /// <summary>
    /// Gets all recorded metrics for a run.
    /// </summary>
    public IReadOnlyList<GenerationMetrics> GetRunMetrics(string? runId)
    {
        if (string.IsNullOrEmpty(runId)) return _metrics.ToList();
        return _metrics.Where(m => m.RunId == runId).OrderBy(m => m.TimestampUtc).ToList();
    }

    /// <summary>
    /// Calculates aggregate summary for a run.
    /// </summary>
    public (int TotalTokens, double AvgTokensPerSec, int OverrunCount) GetRunSummary(string? runId)
    {
        var list = GetRunMetrics(runId);
        if (list.Count == 0) return (0, 0.0, 0);

        int total = list.Sum(m => m.InputTokens + m.OutputTokens + m.ThinkingTokens);
        double avgSpeed = list.Average(m => m.TokensPerSecond);
        int overruns = list.Count(m => m.IsReasoningOverrun);

        return (total, avgSpeed, overruns);
    }
}
