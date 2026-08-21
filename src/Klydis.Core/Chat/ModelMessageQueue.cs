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
/// Represents an attached file, image, screenshot, audio snippet, or text context snippet associated with a queued message.
/// </summary>
public record QueuedMessageAttachment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Type { get; init; } = "File"; // File, Image, Screenshot, Audio, TextContext
    public string SizeDisplay { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
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
    public List<QueuedMessageAttachment> Attachments { get; init; } = new();
    public QueuedMessageMode Mode { get; init; } = QueuedMessageMode.Steer;
    public QueuedMessageStatus Status { get; set; } = QueuedMessageStatus.Queued;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The task this message belongs to, when known. Stamped at enqueue time with the
    /// session's current task, so the model only ever sees the CURRENT task's queued items
    /// (hard isolation boundary). Null for legacy rows / pre-task enqueues, which fall back
    /// to session-scoped visibility.
    /// </summary>
    public string? TaskId { get; init; }

    /// <summary>
    /// Explicit position in the session's processing order (0 = first). Set on enqueue and
    /// renormalized by <see cref="ModelMessageQueue.Reorder"/>; persisted so a drag-and-drop
    /// reordering survives restarts. Items with equal position fall back to CreatedAt (FIFO),
    /// which keeps legacy rows without a meaningful position in their original order.
    /// </summary>
    public int Position { get; set; }

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
                // NOTE: a Processing row surviving a restart is a stale lease (the claimer
                // died mid-delivery). Reclaiming it requires a claim timestamp + lease
                // duration so a fresh in-flight claim is not double-delivered — deferred to
                // the lease-expiry work (schema migration); delivery-failure release already
                // flows through MarkStatus Processing -> Queued.
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
    /// Enqueues a new message for processing or steering, optionally with contextual attachments.
    /// </summary>
    public QueuedMessage Enqueue(
        string sessionId,
        string content,
        IEnumerable<QueuedMessageAttachment>? attachments = null,
        QueuedMessageMode mode = QueuedMessageMode.Steer,
        string? taskId = null)
    {
        var attachmentList = attachments?.ToList() ?? new List<QueuedMessageAttachment>();

        if (string.IsNullOrWhiteSpace(content) && attachmentList.Count == 0)
            throw new ArgumentException("Queued message must contain text content or at least one contextual attachment.", nameof(content));

        EnsureLoaded();

        var msg = new QueuedMessage
        {
            SessionId = sessionId ?? string.Empty,
            Content = content ?? string.Empty,
            Attachments = attachmentList,
            Mode = mode,
            Status = QueuedMessageStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            TaskId = taskId
        };

        lock (_lock)
        {
            // Append at the end of the session's queued items (next free position).
            msg.Position = _queue.Count(m => m.SessionId == msg.SessionId && m.Status == QueuedMessageStatus.Queued);
            _queue.Add(msg);
        }

        Persist(msg);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        return msg;
    }

    /// <summary>
    /// Convenience overload for enqueuing text-only messages without attachments.
    /// </summary>
    public QueuedMessage Enqueue(string sessionId, string content, QueuedMessageMode mode, string? taskId = null)
        => Enqueue(sessionId, content, attachments: null, mode: mode, taskId: taskId);

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
    /// <summary>
    /// Processing order for a session's queued messages: explicit <see cref="QueuedMessage.Position"/>
    /// first (drag-and-drop reorder), then CreatedAt (FIFO tiebreak for equal positions).
    /// </summary>
    private static IEnumerable<QueuedMessage> InProcessingOrder(IEnumerable<QueuedMessage> items)
        => items.OrderBy(m => m.Position).ThenBy(m => m.CreatedAt);

    public IReadOnlyList<QueuedMessage> GetPending(string sessionId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return InProcessingOrder(_queue
                .Where(m => m.SessionId == sessionId && m.Status == QueuedMessageStatus.Queued))
                .ToList();
        }
    }

    /// <summary>
    /// Pending queued messages for a session scoped to ONE task. This is the isolation
    /// boundary the model sees: items stamped with a different task (or legacy items with no
    /// task) are not offered to the model as obligations of the current task. Callers pass
    /// the task id the current turn resolved to; a null task id degrades to the full
    /// session view (legacy behavior).
    /// </summary>
    public IReadOnlyList<QueuedMessage> GetPending(string sessionId, string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return GetPending(sessionId);
        }
        EnsureLoaded();
        lock (_lock)
        {
            return InProcessingOrder(_queue
                .Where(m => m.SessionId == sessionId && m.Status == QueuedMessageStatus.Queued && m.TaskId == taskId))
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
            return InProcessingOrder(_queue
                .Where(m => m.SessionId == sessionId && m.Mode == QueuedMessageMode.Steer && m.Status == QueuedMessageStatus.Queued))
                .ToList();
        }
    }

    /// <summary>
    /// Pending steer messages scoped to one task — the task-isolated variant used by the
    /// incorporate_queued_message tool so a new task's run cannot incorporate an old task's
    /// queued message. Null task id degrades to the session view.
    /// </summary>
    public IReadOnlyList<QueuedMessage> GetPendingSteer(string sessionId, string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return GetPendingSteer(sessionId);
        }
        EnsureLoaded();
        lock (_lock)
        {
            return InProcessingOrder(_queue
                .Where(m => m.SessionId == sessionId && m.Mode == QueuedMessageMode.Steer && m.Status == QueuedMessageStatus.Queued && m.TaskId == taskId))
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
            return InProcessingOrder(_queue
                .Where(m => m.SessionId == sessionId && m.Mode == QueuedMessageMode.DirectSend && m.Status == QueuedMessageStatus.Queued))
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
            return InProcessingOrder(_queue
                .Where(m => m.SessionId == sessionId && m.Status == QueuedMessageStatus.Queued))
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
                // Processing → Queued is the lease-expiry/retry path: a claim whose delivery
                // failed is released back to the queue (AttemptCount already incremented, so
                // redelivery is observable).
                bool valid = status switch
                {
                    QueuedMessageStatus.Processing => msg.Status == QueuedMessageStatus.Queued,
                    QueuedMessageStatus.Queued => msg.Status == QueuedMessageStatus.Processing,
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
    /// Moves a queued message to a new position in its session's processing order (drag-and-drop
    /// reorder). The session's queued items are re-sequenced 0..n-1 and every changed row is
    /// persisted, so the order survives restarts. Returns false when the move is a no-op or the
    /// message is not a pending queued item.
    /// </summary>
    public bool Reorder(Guid id, int newIndex)
    {
        EnsureLoaded();

        bool updated = false;
        List<QueuedMessage> changed = new();
        lock (_lock)
        {
            var msg = _queue.FirstOrDefault(m => m.Id == id && m.Status == QueuedMessageStatus.Queued);
            if (msg == null) return false;

            var sessionItems = InProcessingOrder(_queue
                .Where(m => m.SessionId == msg.SessionId && m.Status == QueuedMessageStatus.Queued))
                .ToList();
            if (sessionItems.Count < 2) return false;

            int oldIndex = sessionItems.IndexOf(msg);
            newIndex = Math.Clamp(newIndex, 0, sessionItems.Count - 1);
            if (oldIndex == newIndex) return false;

            sessionItems.RemoveAt(oldIndex);
            sessionItems.Insert(newIndex, msg);
            for (int i = 0; i < sessionItems.Count; i++)
            {
                if (sessionItems[i].Position != i)
                {
                    sessionItems[i].Position = i;
                    changed.Add(sessionItems[i]);
                }
            }
            updated = changed.Count > 0;
        }

        if (updated)
        {
            foreach (var item in changed)
            {
                Persist(item);
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
    /// Removes a queued message by ID — the delivery ACK. The in-memory entry is dropped and
    /// the durable row is deleted so a delivered message can never be re-hydrated as stale
    /// work after a restart (a previously observed orphan: items delivered and left in
    /// Processing, resurrected on every launch and skipped by the sequencer's status filter).
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
            if (_store != null)
            {
                try
                {
                    _store.DeleteQueuedMessageAsync(id).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete durable queued message {Id}.", id);
                }
            }
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
