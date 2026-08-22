using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Klydis.Core.Tracing;

/// <summary>
/// Universal interface for recording, persisting, and querying correlated agent trace events.
/// All agent execution subsystems emit events through this interface.
/// </summary>
public interface IAgentTrace
{
    /// <summary>
    /// Records a trace event synchronously. Must never throw or block the caller.
    /// </summary>
    void Record(AgentTraceEvent evt);

    /// <summary>
    /// Records a trace event asynchronously to persistent storage.
    /// </summary>
    Task RecordAsync(AgentTraceEvent evt);

    /// <summary>
    /// Retrieves all trace events for a session, ordered chronologically.
    /// </summary>
    Task<IReadOnlyList<AgentTraceEvent>> GetEventsBySessionAsync(string sessionId, int limit = 10000);

    /// <summary>
    /// Retrieves all trace events for a specific task, ordered chronologically.
    /// </summary>
    Task<IReadOnlyList<AgentTraceEvent>> GetEventsByTaskAsync(string taskId, int limit = 10000);

    /// <summary>
    /// Retrieves all trace events for a specific run, ordered chronologically.
    /// </summary>
    Task<IReadOnlyList<AgentTraceEvent>> GetEventsByRunAsync(string runId, int limit = 10000);
}
