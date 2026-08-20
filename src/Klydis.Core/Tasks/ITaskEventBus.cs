using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Memory;

namespace Klydis.Core.Tasks;

/// <summary>
/// In-process event bus for reactive execution event pub-sub (Phase 11).
/// Enables real-time UI streaming without 2-second periodic database polling.
/// </summary>
public interface ITaskEventBus
{
    /// <summary>Publishes an execution event to all matching subscribers.</summary>
    void Publish(ExecutionEventRow eventRow);

    /// <summary>Subscribes to execution events, optionally filtered to a specific task.</summary>
    IDisposable Subscribe(Action<ExecutionEventRow> handler, string? taskId = null);
}

/// <summary>
/// Thread-safe in-memory pub-sub implementation of <see cref="ITaskEventBus"/>.
/// </summary>
public sealed class TaskEventBus : ITaskEventBus
{
    private sealed class Subscription : IDisposable
    {
        private readonly TaskEventBus _bus;
        private readonly Action<ExecutionEventRow> _handler;
        private readonly string? _taskId;

        public Subscription(TaskEventBus bus, Action<ExecutionEventRow> handler, string? taskId)
        {
            _bus = bus;
            _handler = handler;
            _taskId = taskId;
        }

        public void Invoke(ExecutionEventRow ev)
        {
            if (_taskId == null || string.Equals(_taskId, ev.TaskId, StringComparison.OrdinalIgnoreCase))
            {
                _handler(ev);
            }
        }

        public void Dispose()
        {
            _bus.Unsubscribe(this);
        }
    }

    private readonly List<Subscription> _subscriptions = new();
    private readonly object _lock = new();

    /// <inheritdoc />
    public void Publish(ExecutionEventRow eventRow)
    {
        if (eventRow == null) return;
        List<Subscription> snapshot;
        lock (_lock)
        {
            snapshot = _subscriptions.ToList();
        }
        foreach (var sub in snapshot)
        {
            try
            {
                sub.Invoke(eventRow);
            }
            catch
            {
                // Subscriber exception must not interrupt publishing to other subscribers
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<ExecutionEventRow> handler, string? taskId = null)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var sub = new Subscription(this, handler, taskId);
        lock (_lock)
        {
            _subscriptions.Add(sub);
        }
        return sub;
    }

    private void Unsubscribe(Subscription sub)
    {
        lock (_lock)
        {
            _subscriptions.Remove(sub);
        }
    }
}
