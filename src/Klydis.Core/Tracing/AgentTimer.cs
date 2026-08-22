using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Klydis.Core.Tracing;

/// <summary>
/// Active scope for a monotonic timing measurement. On disposal, emits a completion trace event.
/// </summary>
public sealed class AgentOperationScope : IDisposable
{
    private readonly IAgentTrace? _trace;
    private readonly string _operation;
    private readonly AgentTimingCategory _category;
    private readonly string? _sessionId;
    private readonly string? _taskId;
    private readonly string? _runId;
    private readonly string? _turnId;
    private readonly string? _generationId;
    private readonly string? _toolExecutionId;
    private readonly Dictionary<string, object?> _data;
    private readonly long _startMonotonic;
    private readonly DateTimeOffset _startedAtUtc;
    private bool _isCompleted;
    private double _elapsedMs;

    public AgentOperationScope(
        IAgentTrace? trace,
        string operation,
        AgentTimingCategory category,
        string? sessionId = null,
        string? taskId = null,
        string? runId = null,
        string? turnId = null,
        string? generationId = null,
        string? toolExecutionId = null,
        Dictionary<string, object?>? data = null)
    {
        _trace = trace;
        _operation = operation;
        _category = category;
        _sessionId = sessionId;
        _taskId = taskId;
        _runId = runId;
        _turnId = turnId;
        _generationId = generationId;
        _toolExecutionId = toolExecutionId;
        _data = data != null ? new Dictionary<string, object?>(data) : new Dictionary<string, object?>();
        _startedAtUtc = DateTimeOffset.UtcNow;
        _startMonotonic = Stopwatch.GetTimestamp();

        // Emit Started event
        EmitEvent(GetStartedEventType(operation), null, _startedAtUtc);
    }

    public DateTimeOffset StartedAtUtc => _startedAtUtc;
    public double ElapsedMilliseconds => (_isCompleted ? _elapsedMs : (Stopwatch.GetTimestamp() - _startMonotonic) * 1000.0 / Stopwatch.Frequency);

    public void SetData(string key, object? value)
    {
        lock (_data)
        {
            _data[key] = value;
        }
    }

    public void Complete(Dictionary<string, object?>? additionalData = null)
    {
        if (_isCompleted) return;
        _isCompleted = true;

        long endMonotonic = Stopwatch.GetTimestamp();
        DateTimeOffset endedAtUtc = DateTimeOffset.UtcNow;
        _elapsedMs = (endMonotonic - _startMonotonic) * 1000.0 / Stopwatch.Frequency;

        if (additionalData != null)
        {
            lock (_data)
            {
                foreach (var (k, v) in additionalData)
                {
                    _data[k] = v;
                }
            }
        }

        lock (_data)
        {
            _data["operation"] = _operation;
            _data["category"] = _category.ToString();
            _data["started_at_utc"] = _startedAtUtc.ToString("o");
            _data["completed_at_utc"] = endedAtUtc.ToString("o");
            _data["duration_ms"] = _elapsedMs;
        }

        EmitEvent(GetCompletedEventType(_operation), _elapsedMs, endedAtUtc);
    }

    public void Dispose()
    {
        Complete();
    }

    private void EmitEvent(TraceEventType type, double? durationMs, DateTimeOffset timestamp)
    {
        if (_trace == null) return;

        try
        {
            Dictionary<string, object?> dataCopy;
            lock (_data)
            {
                dataCopy = new Dictionary<string, object?>(_data);
            }

            var evt = new AgentTraceEvent
            {
                EventId = $"evt_{Guid.NewGuid():N}",
                TimestampUtc = timestamp,
                MonotonicTimestamp = Stopwatch.GetTimestamp(),
                Type = type,
                Category = _category,
                DurationMs = durationMs,
                SessionId = _sessionId,
                TaskId = _taskId,
                RunId = _runId,
                TurnId = _turnId,
                GenerationId = _generationId,
                ToolExecutionId = _toolExecutionId,
                Data = dataCopy
            };

            _trace.Record(evt);
        }
        catch { /* best effort */ }
    }

    private static TraceEventType GetStartedEventType(string operation) => operation.ToLowerInvariant() switch
    {
        "turn" => TraceEventType.TurnStarted,
        "cycle" => TraceEventType.CycleStarted,
        "run" => TraceEventType.RunStarted,
        "context_build" or "context" => TraceEventType.ContextBuildStarted,
        "model" or "inference" or "model_inference" => TraceEventType.InferenceStarted,
        "generation" => TraceEventType.GenerationStarted,
        "tool" or "tool_execution" => TraceEventType.ToolExecutionStarted,
        "skill" => TraceEventType.SkillInvocationStarted,
        "web" or "web_search" => TraceEventType.WebSearchStarted,
        "scrape" => TraceEventType.ScrapeStarted,
        "compaction" => TraceEventType.CompactionStarted,
        "verification" or "verify" => TraceEventType.VerificationStarted,
        "planning" or "plan" => TraceEventType.PlanCreated,
        _ => TraceEventType.GenerationStarted
    };

    private static TraceEventType GetCompletedEventType(string operation) => operation.ToLowerInvariant() switch
    {
        "turn" => TraceEventType.TurnCompleted,
        "cycle" => TraceEventType.CycleCompleted,
        "run" => TraceEventType.RunCompleted,
        "context_build" or "context" => TraceEventType.ContextBuilt,
        "model" or "inference" or "model_inference" => TraceEventType.InferenceCompleted,
        "generation" => TraceEventType.GenerationCompleted,
        "tool" or "tool_execution" => TraceEventType.ToolExecutionCompleted,
        "skill" => TraceEventType.SkillInvocationCompleted,
        "web" or "web_search" => TraceEventType.WebSearchCompleted,
        "scrape" => TraceEventType.ScrapeCompleted,
        "compaction" => TraceEventType.CompactionCompleted,
        "verification" or "verify" => TraceEventType.VerificationCompleted,
        "planning" or "plan" => TraceEventType.PlanCreated,
        _ => TraceEventType.GenerationCompleted
    };
}

/// <summary>
/// Production implementation of <see cref="IAgentTimer"/> that produces <see cref="AgentOperationScope"/> instances.
/// </summary>
public sealed class AgentTimer : IAgentTimer
{
    private readonly IAgentTrace? _trace;

    public AgentTimer(IAgentTrace? trace = null)
    {
        _trace = trace;
    }

    /// <inheritdoc/>
    public AgentOperationScope Start(
        string operation,
        AgentTimingCategory category,
        string? sessionId = null,
        string? taskId = null,
        string? runId = null,
        string? turnId = null,
        string? generationId = null,
        string? toolExecutionId = null,
        Dictionary<string, object?>? data = null)
    {
        return new AgentOperationScope(
            _trace,
            operation,
            category,
            sessionId,
            taskId,
            runId,
            turnId,
            generationId,
            toolExecutionId,
            data);
    }

    /// <inheritdoc/>
    public void RecordDuration(
        string operation,
        AgentTimingCategory category,
        double durationMs,
        string? sessionId = null,
        string? taskId = null,
        string? runId = null,
        string? turnId = null,
        string? generationId = null,
        Dictionary<string, object?>? data = null)
    {
        if (_trace == null) return;

        var mergedData = data != null ? new Dictionary<string, object?>(data) : new Dictionary<string, object?>();
        mergedData["operation"] = operation;
        mergedData["category"] = category.ToString();
        mergedData["duration_ms"] = durationMs;

        var evt = AgentTraceEvent.Create(
            TraceEventType.GenerationCompleted,
            sessionId: sessionId,
            taskId: taskId,
            runId: runId,
            turnId: turnId,
            generationId: generationId,
            category: category,
            durationMs: durationMs,
            data: mergedData);

        _trace.Record(evt);
    }
}
