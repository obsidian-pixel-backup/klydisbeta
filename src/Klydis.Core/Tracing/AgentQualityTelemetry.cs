using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tracing;

public sealed record ExecutionMetricsReport(
    int TotalTurns,
    int TotalGenerations,
    int TotalActions,
    int ProductiveActions,
    int UnproductiveActions,
    double ProductiveActionRatio,
    int ToolSuccesses,
    int ToolFailures,
    int ToolRejections,
    double ToolSuccessRate,
    int CompletedObjectives,
    int TotalObjectives,
    double ObjectiveCompletionRate,
    TimeSpan TotalWallTime,
    TimeSpan ToolTime,
    TimeSpan InferenceTime,
    TimeSpan UnproductiveTime,
    int AgentQualityScore,
    string QualityGrade);

/// <summary>
/// Computes runtime execution quality, timing breakdown, productive action ratio,
/// and holistic Agent Quality Score.
/// </summary>
public static class AgentQualityTelemetry
{
    public static ExecutionMetricsReport ComputeReport(
        List<AgentTraceEvent> events,
        int totalObjectives = 1,
        int completedObjectives = 1,
        TimeSpan? wallTimeOverride = null)
    {
        int turns = events.Count(e => e.Type is TraceEventType.TurnStarted or TraceEventType.TurnCompleted) / 2;
        if (turns == 0) turns = 1;

        int generations = events.Count(e => e.Type is TraceEventType.GenerationStarted or TraceEventType.GenerationCompleted or TraceEventType.RawModelOutput);

        // Single-layer tool accounting (P0): successes/failures count only PHYSICAL terminal
        // events (ToolExecutionCompleted/Failed); gate rejections (ToolCallRejected) are a
        // separate logical layer and are NEVER folded into failures — mixing them produced
        // ambiguous "13 successes / 16 failures" reports where the two layers disagreed.
        var toolStarts = events.Where(e => e.Type == TraceEventType.ToolExecutionStarted).ToList();
        var toolSuccessEvents = events.Where(e => e.Type == TraceEventType.ToolExecutionCompleted).ToList();
        var toolFailEvents = events.Where(e => e.Type == TraceEventType.ToolExecutionFailed).ToList();
        var toolRejectionEvents = events.Where(e => e.Type == TraceEventType.ToolCallRejected).ToList();

        int toolSuccesses = toolSuccessEvents.Count;
        int toolFailures = toolFailEvents.Count;
        int toolRejections = toolRejectionEvents.Count;
        int totalActions = Math.Max(toolStarts.Count, toolSuccesses + toolFailures);

        // Productive action estimation:
        // A tool call is productive if it succeeded AND was not an aimless repetitive exploratory action
        int productiveActions = 0;
        int unproductiveActions = 0;

        foreach (var evt in events.Where(e => e.Type is TraceEventType.ToolExecutionCompleted or TraceEventType.ToolExecutionFailed or TraceEventType.ToolCallRejected))
        {
            bool isSuccess = evt.Type == TraceEventType.ToolExecutionCompleted;
            string tool = evt.Data?.TryGetValue("tool", out var t) == true ? t?.ToString() ?? "" : "";

            if (isSuccess && tool is not "task_progress")
            {
                productiveActions++;
            }
            else
            {
                unproductiveActions++;
            }
        }

        if (totalActions == 0)
        {
            totalActions = productiveActions + unproductiveActions;
        }

        double productiveRatio = totalActions > 0 ? (double)productiveActions / totalActions : 1.0;
        double toolSuccessRate = totalActions > 0 ? (double)toolSuccesses / totalActions : 1.0;
        double objCompletionRate = totalObjectives > 0 ? (double)completedObjectives / totalObjectives : 1.0;

        // Compute timing from trace events
        long totalToolMs = 0;
        long totalInferenceMs = 0;

        foreach (var evt in events)
        {
            if (evt.Data != null)
            {
                if (evt.Data.TryGetValue("duration_ms", out var dMs) && dMs != null)
                {
                    if (long.TryParse(dMs.ToString(), out long ms))
                    {
                        if (evt.Type is TraceEventType.ToolExecutionCompleted or TraceEventType.ToolExecutionFailed)
                        {
                            totalToolMs += ms;
                        }
                        else if (evt.Type is TraceEventType.GenerationCompleted or TraceEventType.TurnCompleted)
                        {
                            totalInferenceMs += ms;
                        }
                    }
                }
            }
        }

        TimeSpan toolDuration = TimeSpan.FromMilliseconds(totalToolMs);
        TimeSpan inferenceDuration = TimeSpan.FromMilliseconds(totalInferenceMs);

        DateTimeOffset? firstEvt = events.FirstOrDefault()?.Timestamp;
        DateTimeOffset? lastEvt = events.LastOrDefault()?.Timestamp;
        TimeSpan wallDuration = wallTimeOverride ?? (firstEvt.HasValue && lastEvt.HasValue && lastEvt > firstEvt
            ? lastEvt.Value - firstEvt.Value
            : toolDuration + inferenceDuration);

        TimeSpan unproductiveDuration = TimeSpan.FromMilliseconds(
            Math.Max(0, (unproductiveActions * (totalActions > 0 ? totalToolMs / totalActions : 100))));

        // Quality Score calculation: 0..100
        // - Objective completion: 40 points
        // - Tool success rate: 25 points
        // - Productive action ratio: 25 points
        // - Penalty for high unproductive count: up to 10 points
        int score = (int)Math.Round(
            (objCompletionRate * 40.0) +
            (toolSuccessRate * 25.0) +
            (productiveRatio * 25.0) +
            (unproductiveActions == 0 ? 10.0 : Math.Max(0, 10.0 - unproductiveActions * 2.0)));

        score = Math.Clamp(score, 0, 100);

        string grade = score switch
        {
            >= 90 => "A (Optimal Autonomous Execution)",
            >= 80 => "B (Reliable Agentic Execution)",
            >= 65 => "C (Acceptable with Moderate Recoveries)",
            >= 50 => "D (Degraded / High Retry Rate)",
            _ => "F (Agentic Loop / Ineffective Execution)"
        };

        return new ExecutionMetricsReport(
            TotalTurns: turns,
            TotalGenerations: generations,
            TotalActions: totalActions,
            ProductiveActions: productiveActions,
            UnproductiveActions: unproductiveActions,
            ProductiveActionRatio: Math.Round(productiveRatio, 2),
            ToolSuccesses: toolSuccesses,
            ToolFailures: toolFailures,
            ToolRejections: toolRejections,
            ToolSuccessRate: Math.Round(toolSuccessRate, 2),
            CompletedObjectives: completedObjectives,
            TotalObjectives: totalObjectives,
            ObjectiveCompletionRate: Math.Round(objCompletionRate, 2),
            TotalWallTime: wallDuration,
            ToolTime: toolDuration,
            InferenceTime: inferenceDuration,
            UnproductiveTime: unproductiveDuration,
            AgentQualityScore: score,
            QualityGrade: grade);
    }
}
