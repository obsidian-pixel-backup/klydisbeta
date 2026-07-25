using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

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
/// Represents a message waiting in the model processing queue.
/// </summary>
public record QueuedMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SessionId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public QueuedMessageMode Mode { get; init; } = QueuedMessageMode.Steer;
    public QueuedMessageStatus Status { get; set; } = QueuedMessageStatus.Queued;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Thread-safe queue manager for model requests, steering instructions, and direct sends.
/// </summary>
public class ModelMessageQueue
{
    private readonly object _lock = new();
    private readonly List<QueuedMessage> _queue = new();

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

        QueueChanged?.Invoke(this, EventArgs.Empty);
        return msg;
    }

    /// <summary>
    /// Gets all pending queued messages for a given session.
    /// </summary>
    public IReadOnlyList<QueuedMessage> GetPending(string sessionId)
    {
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
        lock (_lock)
        {
            return _queue
                .Where(m => m.SessionId == sessionId && m.Status == QueuedMessageStatus.Queued)
                .OrderBy(m => m.CreatedAt)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Finds a queued message by ID.
    /// </summary>
    public QueuedMessage? GetById(Guid id)
    {
        lock (_lock)
        {
            return _queue.FirstOrDefault(m => m.Id == id);
        }
    }

    /// <summary>
    /// Updates status of a queued message.
    /// </summary>
    public bool MarkStatus(Guid id, QueuedMessageStatus status)
    {
        bool updated = false;
        lock (_lock)
        {
            var msg = _queue.FirstOrDefault(m => m.Id == id);
            if (msg != null)
            {
                msg.Status = status;
                updated = true;
            }
        }

        if (updated)
        {
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
    /// Clears all queued messages for a session.
    /// </summary>
    public void Clear(string sessionId)
    {
        bool changed = false;
        lock (_lock)
        {
            int removedCount = _queue.RemoveAll(m => m.SessionId == sessionId);
            changed = removedCount > 0;
        }

        if (changed)
        {
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets all queued messages regardless of session (for debugging/monitoring).
    /// </summary>
    public IReadOnlyList<QueuedMessage> GetAll()
    {
        lock (_lock)
        {
            return _queue.ToList();
        }
    }
}
