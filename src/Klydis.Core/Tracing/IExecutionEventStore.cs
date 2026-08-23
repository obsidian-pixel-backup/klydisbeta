using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.Tracing;

/// <summary>
/// Authoritative store and broadcaster for <see cref="ExecutionEvent"/> records.
/// </summary>
public interface IExecutionEventStore
{
    /// <summary>Generates the next monotonic total sequence number.</summary>
    long NextSequenceNumber();

    /// <summary>Appends and broadcasts a new execution event synchronously.</summary>
    void RecordEvent(ExecutionEvent evt);

    /// <summary>Appends and broadcasts a new execution event asynchronously.</summary>
    Task RecordEventAsync(ExecutionEvent evt);

    /// <summary>Retrieves all execution events for a session in monotonic order.</summary>
    IReadOnlyList<ExecutionEvent> GetSessionEvents(string sessionId);

    /// <summary>Retrieves all execution events for a task in monotonic order.</summary>
    IReadOnlyList<ExecutionEvent> GetTaskEvents(string taskId);

    /// <summary>Fired whenever a new event is recorded.</summary>
    event Action<ExecutionEvent>? EventAppended;
}

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IExecutionEventStore"/> with monotonic sequence numbering.
/// </summary>
public sealed class InMemoryExecutionEventStore : IExecutionEventStore
{
    private long _sequenceCounter = 0;
    private readonly ConcurrentDictionary<string, List<ExecutionEvent>> _sessionEvents = new();
    private readonly ConcurrentDictionary<string, List<ExecutionEvent>> _taskEvents = new();
    private readonly object _lock = new();

    public event Action<ExecutionEvent>? EventAppended;

    public long NextSequenceNumber()
    {
        return Interlocked.Increment(ref _sequenceCounter);
    }

    public void RecordEvent(ExecutionEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Ensure sequence number is populated monotonically
        if (evt.SequenceNumber <= 0)
        {
            evt = evt with { SequenceNumber = NextSequenceNumber() };
        }

        lock (_lock)
        {
            if (!string.IsNullOrEmpty(evt.SessionId))
            {
                var list = _sessionEvents.GetOrAdd(evt.SessionId, _ => new List<ExecutionEvent>());
                list.Add(evt);
            }

            if (!string.IsNullOrEmpty(evt.TaskId))
            {
                var list = _taskEvents.GetOrAdd(evt.TaskId, _ => new List<ExecutionEvent>());
                list.Add(evt);
            }
        }

        EventAppended?.Invoke(evt);
    }

    public Task RecordEventAsync(ExecutionEvent evt)
    {
        RecordEvent(evt);
        return Task.CompletedTask;
    }

    public IReadOnlyList<ExecutionEvent> GetSessionEvents(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return Array.Empty<ExecutionEvent>();

        lock (_lock)
        {
            if (_sessionEvents.TryGetValue(sessionId, out var list))
            {
                return list.OrderBy(e => e.SequenceNumber).ToList();
            }
        }
        return Array.Empty<ExecutionEvent>();
    }

    public IReadOnlyList<ExecutionEvent> GetTaskEvents(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return Array.Empty<ExecutionEvent>();

        lock (_lock)
        {
            if (_taskEvents.TryGetValue(taskId, out var list))
            {
                return list.OrderBy(e => e.SequenceNumber).ToList();
            }
        }
        return Array.Empty<ExecutionEvent>();
    }
}
