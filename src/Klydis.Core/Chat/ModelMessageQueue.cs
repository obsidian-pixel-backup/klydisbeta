using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Klydis.Core.Memory;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Chat;

/// <summary>
/// Mode specifying how a queued message should be processed.
/// </summary>
public enum QueuedMessageMode
{
    /// <summary>
    /// Process message sequentially as a standard user turn after current generation completes.
    /// </summary>
    DirectSend,

    /// <summary>
    /// Allow the model to inspect and incorporate the message at the optimal time to steer execution.
    /// </summary>
    Steer
}

/// <summary>
/// Status of a queued message.
/// </summary>
public enum QueuedMessageStatus
{
    Queued,
    Processing,
    Incorporated,
    Cancelled
}

/// <summary>
/// Represents a message waiting in the model processing queue. The stable <see cref="Id"/>
/// doubles as the idempotency key: a re-delivered message (after a crash) can be identified
/// and its duplicate execution skipped. <see cref="AttemptCount"/> is the lease signal —
/// incremented each time the message is claimed for processing, so at-least-once delivery
/// (crash between tool success and queue ACK) is observable and recoverable.
/// </summary>
public record QueuedMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SessionId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public QueuedMessageMode Mode { get; init; } = QueuedMessageMode.Steer;
    public QueuedMessageStatus Status { get; set; } = QueuedMessageStatus.Queued;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Number of times this message has been claimed for processing (lease renewals).
    /// Persisted so it survives restarts.
    /// </summary>
    public int AttemptCount { get; set; }
}

/// <summary>
/// Thread-safe queue manager for model requests, steering instructions, and direct sends.
/// </summary>
public class ModelMessageQueue
{
    private readonly object _lock = new();
    private readonly List<QueuedMessage> _queue = new();
    private readonly MessageStore? _store;
    private readonly ILogger<ModelMessageQueue>? _logger;
    private bool _hydrated;

    /// <summary>
    /// Creates the queue. When a <paramref name="store"/> is provided the queue is durable:
    /// entries are persisted to SQLite on enqueue/status changes and hydrated on first access,
    /// so queued work survives process restarts and model-terminated turns.
    /// </summary>
    public ModelMessageQueue(MessageStore? store = null, ILogger<ModelMessageQueue>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Hydrates the in-memory queue from the durable store exactly once (lazy). A hydration
    /// failure degrades to an empty queue — persistence must never block the UI or the loop.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_hydrated || _store == null) return;
        lock (_lock)
        {
            if (_hydrated || _store == null) return;
            _hydrated = true;
            try
            {
                var persisted = _store.LoadQueuedMessagesAsync().GetAwaiter().GetResult();
                foreach (var msg in persisted)
                {
                    if (!_queue.Any(m => m.Id == msg.Id))
                    {
                        _queue.Add(msg);
                    }
                }
                if (persisted.Count > 0)
                {
                    _logger?.LogInformation("Hydrated {Count} queued message(s) from durable store.", persisted.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to hydrate queued messages from durable store.");
            }
        }
    }
    /// <summary>
    /// Triggered whenever the queue contents or message statuses change.
    /// </summary>
    public event EventHandler? QueueChanged;

    /// <summary>
    /// Enqueues a new message for processing or steering.
    /// </summary>
    public QueuedMessage Enqueue(string sessionId, string content, QueuedMessageMode mode = QueuedMessageMode.Steer)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Queued content cannot be empty.", nameof(content));

        EnsureLoaded();

        var msg = new QueuedMessage
        {
            SessionId = sessionId ?? string.Empty,
            Content = content,
            Mode = mode,
            Status = QueuedMessageStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            _queue.Add(msg);
        }

        Persist(msg);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        return msg;
    }

    /// <summary>
    /// Best-effort durable write. Local SQLite writes are fast and sync-over-async is safe
    /// here (Microsoft.Data.Sqlite never posts continuations back to a captured sync context);
    /// a failure is logged and never thrown to the caller.
    /// </summary>
    private void Persist(QueuedMessage msg)
    {
        if (_store == null) return;
        try
        {
            _store.SaveQueuedMessageAsync(msg).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist queued message {Id}.", msg.Id);
        }
    }

    /// <summary>
    /// Gets all pending queued messages for a given session.
    /// </summary>
    public IReadOnlyList<QueuedMessage> GetPending(string sessionId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _queue
                .Where(m => m.SessionId == sessionId && m.Status == QueuedMessageStatus.Queued)
                .OrderBy(m => m.CreatedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Gets pending steer messages for a given session.
    /// </summary>
    public IReadOnlyList<QueuedMessage> GetPendingSteer(string sessionId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _queue
                .Where(m => m.SessionId == sessionId && m.Mode == QueuedMessageMode.Steer && m.Status == QueuedMessageStatus.Queued)
                .OrderBy(m => m.CreatedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Gets the next pending DirectSend message for sequential turn execution.
    /// </summary>
    public QueuedMessage? GetNextDirectSend(string sessionId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _queue
                .Where(m => m.SessionId == sessionId && m.Mode == QueuedMessageMode.DirectSend && m.Status == QueuedMessageStatus.Queued)
                .OrderBy(m => m.CreatedAt)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Gets the next pending queued message regardless of mode for fallback sequential turn execution.
    /// </summary>
    public QueuedMessage? GetNextPending(string sessionId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _queue
                .Where(m => m.SessionId == sessionId && m.Status == QueuedMessageStatus.Queued)
                .OrderBy(m => m.CreatedAt)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Finds a queued message by ID, optionally scoped to a session.
    /// </summary>
    public QueuedMessage? GetById(Guid id, string? sessionId = null)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _queue.FirstOrDefault(m => m.Id == id && (string.IsNullOrEmpty(sessionId) || m.SessionId == sessionId));
        }
    }

    /// <summary>
    /// Updates status of a queued message with a transition guard (Queued → Processing →
    /// Incorporated/Cancelled) so a message cannot be processed twice — the Id is the
    /// idempotency key. Claiming (→ Processing) increments the lease attempt count.
    /// Terminal states are removed from the in-memory queue AND the durable store (the work
    /// was ACKed only after it was committed).
    /// </summary>
    public bool MarkStatus(Guid id, QueuedMessageStatus status)
    {
        EnsureLoaded();

        bool updated = false;
        QueuedMessage? terminal = null;
        lock (_lock)
        {
            var msg = _queue.FirstOrDefault(m => m.Id == id);
            if (msg != null)
            {
                // Idempotency guard: only valid forward transitions are allowed. A message
                // already Incorporated (ACKed) can never be claimed or re-incorporated.
                bool valid = status switch
                {
                    QueuedMessageStatus.Processing => msg.Status == QueuedMessageStatus.Queued,
                    QueuedMessageStatus.Incorporated => msg.Status == QueuedMessageStatus.Processing || msg.Status == QueuedMessageStatus.Queued,
                    QueuedMessageStatus.Cancelled => msg.Status == QueuedMessageStatus.Queued || msg.Status == QueuedMessageStatus.Processing,
                    _ => false
                };
                if (valid)
                {
                    msg.Status = status;
                    if (status == QueuedMessageStatus.Processing)
                    {
                        msg.AttemptCount++;
                    }
                    updated = true;
                    if (status == QueuedMessageStatus.Incorporated || status == QueuedMessageStatus.Cancelled)
                    {
                        terminal = msg;
                        _queue.Remove(msg);
                    }
                }
            }
        }

        if (updated)
        {
            if (terminal != null)
            {
                if (_store != null)
                {
                    try
                    {
                        _store.DeleteQueuedMessageAsync(terminal.Id).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to delete queued message {Id} from durable store.", terminal.Id);
                    }
                }
            }
            else
            {
                // Non-terminal transition: persist the new status + attempt count (the lease).
                lock (_lock)
                {
                    var persisted = _queue.FirstOrDefault(m => m.Id == id);
                    if (persisted != null)
                    {
                        Persist(persisted);
                    }
                }
            }
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
        return updated;
    }

    /// <summary>
    /// Toggles the mode of a queued message between DirectSend and Steer.
    /// </summary>
    public bool ToggleMode(Guid id)
    {
        bool updated = false;
        lock (_lock)
        {
            var msg = _queue.FirstOrDefault(m => m.Id == id);
            if (msg != null && msg.Status == QueuedMessageStatus.Queued)
            {
                var newMsg = msg with { Mode = msg.Mode == QueuedMessageMode.Steer ? QueuedMessageMode.DirectSend : QueuedMessageMode.Steer };
                int idx = _queue.IndexOf(msg);
                if (idx >= 0)
                {
                    _queue[idx] = newMsg;
                    updated = true;
                }
            }
        }

        if (updated)
        {
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
        return updated;
    }

    /// <summary>
    /// Removes a queued message by ID.
    /// </summary>
    public bool Remove(Guid id)
    {
        bool removed = false;
        lock (_lock)
        {
            var msg = _queue.FirstOrDefault(m => m.Id == id);
            if (msg != null)
            {
                _queue.Remove(msg);
                removed = true;
            }
        }

        if (removed)
        {
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    /// <summary>
    /// Clears all queued messages for a session (in-memory and durable).
    /// </summary>
    public void Clear(string sessionId)
    {
        EnsureLoaded();

        bool changed = false;
        lock (_lock)
        {
            int removedCount = _queue.RemoveAll(m => m.SessionId == sessionId);
            changed = removedCount > 0;
        }

        if (changed)
        {
            if (_store != null)
            {
                try
                {
                    var persisted = _store.LoadQueuedMessagesAsync().GetAwaiter().GetResult();
                    foreach (var msg in persisted.Where(m => m.SessionId == sessionId))
                    {
                        _store.DeleteQueuedMessageAsync(msg.Id).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to clear durable queued messages for session {SessionId}.", sessionId);
                }
            }
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets all queued messages regardless of session (for debugging/monitoring).
    /// </summary>
    public IReadOnlyList<QueuedMessage> GetAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _queue.ToList();
        }
    }
}
