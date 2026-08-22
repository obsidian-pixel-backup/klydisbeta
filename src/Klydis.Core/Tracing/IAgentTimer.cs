using System;
using System.Collections.Generic;

namespace Klydis.Core.Tracing;

/// <summary>
/// Centralized high-resolution operation timer interface.
/// Provides monotonic duration measurement and automatic trace event emission for agent operations.
/// </summary>
public interface IAgentTimer
{
    /// <summary>
    /// Starts a new timing scope for an operation with monotonic precision.
    /// </summary>
    AgentOperationScope Start(
        string operation,
        AgentTimingCategory category,
        string? sessionId = null,
        string? taskId = null,
        string? runId = null,
        string? turnId = null,
        string? generationId = null,
        string? toolExecutionId = null,
        Dictionary<string, object?>? data = null);

    /// <summary>
    /// Directly records an operation with a known duration.
    /// </summary>
    void RecordDuration(
        string operation,
        AgentTimingCategory category,
        double durationMs,
        string? sessionId = null,
        string? taskId = null,
        string? runId = null,
        string? turnId = null,
        string? generationId = null,
        Dictionary<string, object?>? data = null);
}
