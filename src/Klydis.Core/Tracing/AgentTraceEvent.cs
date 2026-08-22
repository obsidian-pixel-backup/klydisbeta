using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Klydis.Core.Tracing;

/// <summary>
/// Universal correlation envelope representing a single factual event in the agent execution trace.
/// Combines high-resolution monotonic timing (<see cref="Stopwatch.GetTimestamp"/>) for accurate duration
/// measurement with UTC wall-clock timestamps for display and JSONL stream serialization.
/// </summary>
public sealed record AgentTraceEvent
{
    [JsonPropertyName("event_id")]
    public required string EventId { get; init; }

    [JsonPropertyName("timestamp_utc")]
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Legacy/convenience accessor for <see cref="TimestampUtc"/>.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset Timestamp => TimestampUtc;

    /// <summary>
    /// Monotonic timestamp captured via <see cref="Stopwatch.GetTimestamp"/> at event creation.
    /// Used for high-resolution, skew-free elapsed time measurements.
    /// </summary>
    [JsonPropertyName("monotonic_timestamp")]
    public long MonotonicTimestamp { get; init; }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required TraceEventType Type { get; init; }

    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentTimingCategory? Category { get; init; }

    /// <summary>
    /// Duration of the operation in fractional milliseconds, measured monotonically.
    /// </summary>
    [JsonPropertyName("duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DurationMs { get; init; }

    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("task_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskId { get; init; }

    [JsonPropertyName("run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("turn_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; init; }

    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    [JsonPropertyName("action_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActionId { get; init; }

    [JsonPropertyName("tool_execution_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolExecutionId { get; init; }

    [JsonPropertyName("skill_execution_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkillExecutionId { get; init; }

    [JsonPropertyName("artifact_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactId { get; init; }

    [JsonPropertyName("parent_event_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentEventId { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Data { get; init; }

    /// <summary>
    /// Factory helper to build a new trace event with a fresh unique EventId, current UTC timestamp,
    /// and current monotonic timestamp.
    /// </summary>
    public static AgentTraceEvent Create(
        TraceEventType type,
        string? sessionId = null,
        string? taskId = null,
        string? runId = null,
        string? turnId = null,
        string? generationId = null,
        string? actionId = null,
        string? toolExecutionId = null,
        string? skillExecutionId = null,
        string? artifactId = null,
        string? parentEventId = null,
        AgentTimingCategory? category = null,
        double? durationMs = null,
        long? monotonicTimestamp = null,
        Dictionary<string, object?>? data = null)
    {
        return new AgentTraceEvent
        {
            EventId = $"evt_{Guid.NewGuid():N}",
            TimestampUtc = DateTimeOffset.UtcNow,
            MonotonicTimestamp = monotonicTimestamp ?? Stopwatch.GetTimestamp(),
            Type = type,
            Category = category,
            DurationMs = durationMs,
            SessionId = sessionId,
            TaskId = taskId,
            RunId = runId,
            TurnId = turnId,
            GenerationId = generationId,
            ActionId = actionId,
            ToolExecutionId = toolExecutionId,
            SkillExecutionId = skillExecutionId,
            ArtifactId = artifactId,
            ParentEventId = parentEventId,
            Data = data
        };
    }
}
