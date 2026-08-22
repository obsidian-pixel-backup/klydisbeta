using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Diagnostics;
using Klydis.Core.Memory;

namespace Klydis.Core.Tracing;

/// <summary>
/// Thread-safe, persistent implementation of <see cref="IAgentTrace"/>.
/// Collects correlated agent trace events, sanitizes sensitive data via <see cref="TraceSecretRedactor"/>,
/// maintains an in-memory sliding buffer for fast retrieval, and writes durably to SQLite.
/// </summary>
public sealed class AgentTraceService : IAgentTrace
{
    private readonly MessageStore? _messageStore;
    private readonly ILogger<AgentTraceService>? _logger;
    private readonly ConcurrentDictionary<string, List<AgentTraceEvent>> _sessionBuffer = new(StringComparer.Ordinal);
    private const int MaxInMemoryEventsPerSession = 5000;

    public AgentTraceService(MessageStore? messageStore = null, ILogger<AgentTraceService>? logger = null)
    {
        _messageStore = messageStore;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Record(AgentTraceEvent evt)
    {
        var sanitized = SanitizeEvent(evt);
        AddToBuffer(sanitized);

        if (_messageStore != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _messageStore.AddTraceEventAsync(sanitized).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to persist trace event {EventId} ({Type}) to SQLite.", sanitized.EventId, sanitized.Type);
                }
            });
        }
    }

    /// <inheritdoc/>
    public async Task RecordAsync(AgentTraceEvent evt)
    {
        var sanitized = SanitizeEvent(evt);
        AddToBuffer(sanitized);

        if (_messageStore != null)
        {
            try
            {
                await _messageStore.AddTraceEventAsync(sanitized).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to asynchronously persist trace event {EventId} ({Type}) to SQLite.", sanitized.EventId, sanitized.Type);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AgentTraceEvent>> GetEventsBySessionAsync(string sessionId, int limit = 10000)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Array.Empty<AgentTraceEvent>();

        List<AgentTraceEvent> dbEvents = new();
        if (_messageStore != null)
        {
            try
            {
                dbEvents = await _messageStore.GetTraceEventsBySessionAsync(sessionId, limit).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to read trace events from SQLite for session {SessionId}.", sessionId);
            }
        }

        if (_sessionBuffer.TryGetValue(sessionId, out var memoryEvents))
        {
            lock (memoryEvents)
            {
                var seenIds = new HashSet<string>(dbEvents.Select(e => e.EventId), StringComparer.Ordinal);
                foreach (var m in memoryEvents)
                {
                    if (seenIds.Add(m.EventId))
                    {
                        dbEvents.Add(m);
                    }
                }
            }
        }

        return dbEvents.OrderBy(e => e.Timestamp).Take(limit).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AgentTraceEvent>> GetEventsByTaskAsync(string taskId, int limit = 10000)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return Array.Empty<AgentTraceEvent>();

        if (_messageStore != null)
        {
            try
            {
                var dbEvents = await _messageStore.GetTraceEventsByTaskAsync(taskId, limit).ConfigureAwait(false);
                if (dbEvents.Count > 0) return dbEvents;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to read trace events from SQLite for task {TaskId}.", taskId);
            }
        }

        var results = new List<AgentTraceEvent>();
        foreach (var list in _sessionBuffer.Values)
        {
            lock (list)
            {
                results.AddRange(list.Where(e => string.Equals(e.TaskId, taskId, StringComparison.Ordinal)));
            }
        }

        return results.OrderBy(e => e.Timestamp).Take(limit).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AgentTraceEvent>> GetEventsByRunAsync(string runId, int limit = 10000)
    {
        if (string.IsNullOrWhiteSpace(runId)) return Array.Empty<AgentTraceEvent>();

        if (_messageStore != null)
        {
            try
            {
                var dbEvents = await _messageStore.GetTraceEventsByRunAsync(runId, limit).ConfigureAwait(false);
                if (dbEvents.Count > 0) return dbEvents;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to read trace events from SQLite for run {RunId}.", runId);
            }
        }

        var results = new List<AgentTraceEvent>();
        foreach (var list in _sessionBuffer.Values)
        {
            lock (list)
            {
                results.AddRange(list.Where(e => string.Equals(e.RunId, runId, StringComparison.Ordinal)));
            }
        }

        return results.OrderBy(e => e.Timestamp).Take(limit).ToList();
    }

    private static AgentTraceEvent SanitizeEvent(AgentTraceEvent evt)
    {
        if (evt.Data == null || evt.Data.Count == 0) return evt;

        var sanitizedData = TraceSecretRedactor.RedactDictionary(evt.Data);
        return evt with { Data = sanitizedData };
    }

    private void AddToBuffer(AgentTraceEvent evt)
    {
        string key = evt.SessionId ?? evt.TaskId ?? "default";
        var list = _sessionBuffer.GetOrAdd(key, _ => new List<AgentTraceEvent>());
        lock (list)
        {
            list.Add(evt);
            if (list.Count > MaxInMemoryEventsPerSession)
            {
                list.RemoveRange(0, list.Count - MaxInMemoryEventsPerSession);
            }
        }
    }
}
