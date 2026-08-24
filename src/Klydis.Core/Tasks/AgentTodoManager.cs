using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Klydis.Core.Memory;

namespace Klydis.Core.Tasks;

/// <summary>
/// Manages model-generated TODO state per session. TODOs are first-class model state —
/// created via the structured <c>todo.create</c> tool, mutated via <c>todo.update</c>/
/// <c>todo.complete</c>/<c>todo.block</c>/<c>todo.reopen</c>, and persisted durably so a
/// session's pending work survives restarts. The manager owns the authoritative in-memory
/// list and mirrors every mutation to the message store.
/// </summary>
public sealed class AgentTodoManager
{
    private readonly object _lock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<AgentTodo>> _bySession = new();
    private readonly HashSet<string> _loadedSessions = new();
    private readonly MessageStore _messageStore;

    public AgentTodoManager(MessageStore messageStore)
    {
        _messageStore = messageStore;
    }

    /// <summary>
    /// Creates a TODO item for a session. Assigns a stable id when the model did not supply
    /// one, stamps creation time, and persists it before returning.
    /// </summary>
    public async Task<AgentTodo> CreateAsync(string sessionId, AgentTodo todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        string key = sessionId ?? string.Empty;
        await EnsureLoadedAsync(key);

        var created = new AgentTodo
        {
            Id = string.IsNullOrWhiteSpace(todo.Id) ? $"todo-{Guid.NewGuid().ToString("N").Substring(0, 8)}" : todo.Id,
            SessionId = key,
            Title = todo.Title,
            Description = todo.Description,
            Status = TodoStatus.Pending,
            Dependencies = todo.Dependencies,
            RelatedFiles = todo.RelatedFiles,
            ExpectedOutputs = todo.ExpectedOutputs,
            Verification = todo.Verification,
            Purpose = todo.Purpose,
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = null,
            CompletedAt = null,
            PlanTaskId = todo.PlanTaskId,
            BlockedReason = null,
            Evidence = Array.Empty<TodoEvidence>()
        };

        lock (_lock)
        {
            _bySession.GetOrAdd(key, _ => new List<AgentTodo>()).Add(created);
        }
        await _messageStore.SaveAgentTodoAsync(created);
        return created;
    }

    /// <summary>
    /// Transitions a TODO to a new status, stamping lifecycle timestamps, and persists.
    /// Returns null when no TODO with the id exists for the session.
    /// </summary>
    public async Task<AgentTodo?> UpdateStatusAsync(string sessionId, string todoId, TodoStatus status, string? reason = null)
    {
        string key = sessionId ?? string.Empty;
        await EnsureLoadedAsync(key);

        AgentTodo? updated = null;
        lock (_lock)
        {
            var list = _bySession.GetOrAdd(key, _ => new List<AgentTodo>());
            int idx = list.FindIndex(t => t.Id.Equals(todoId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                var t = list[idx];
                updated = new AgentTodo
                {
                    Id = t.Id,
                    SessionId = t.SessionId,
                    Title = t.Title,
                    Description = t.Description,
                    Status = status,
                    Dependencies = t.Dependencies,
                    RelatedFiles = t.RelatedFiles,
                    ExpectedOutputs = t.ExpectedOutputs,
                    Verification = t.Verification,
                    Purpose = t.Purpose,
                    CreatedAt = t.CreatedAt,
                    StartedAt = status == TodoStatus.Running && t.StartedAt == null ? DateTimeOffset.UtcNow : t.StartedAt,
                    CompletedAt = status == TodoStatus.Completed
                        ? DateTimeOffset.UtcNow
                        : (status is TodoStatus.Pending or TodoStatus.Ready or TodoStatus.Running ? null : t.CompletedAt),
                    PlanTaskId = t.PlanTaskId,
                    BlockedReason = status == TodoStatus.Blocked ? (reason ?? t.BlockedReason) : t.BlockedReason,
                    Evidence = t.Evidence
                };
                list[idx] = updated;
            }
        }

        if (updated != null)
        {
            await _messageStore.SaveAgentTodoAsync(updated);
        }
        return updated;
    }

    /// <summary>
    /// Appends an evidence entry to a TODO (e.g. the model's own evidence summary on
    /// <c>todo.complete</c>) and persists. Returns null when the TODO does not exist.
    /// </summary>
    public async Task<AgentTodo?> AddEvidenceAsync(string sessionId, string todoId, TodoEvidence evidence)
    {
        string key = sessionId ?? string.Empty;
        await EnsureLoadedAsync(key);

        AgentTodo? updated = null;
        lock (_lock)
        {
            var list = _bySession.GetOrAdd(key, _ => new List<AgentTodo>());
            int idx = list.FindIndex(t => t.Id.Equals(todoId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                var t = list[idx];
                var evidenceList = t.Evidence.ToList();
                evidenceList.Add(evidence);
                updated = new AgentTodo
                {
                    Id = t.Id,
                    SessionId = t.SessionId,
                    Title = t.Title,
                    Description = t.Description,
                    Status = t.Status,
                    Dependencies = t.Dependencies,
                    RelatedFiles = t.RelatedFiles,
                    ExpectedOutputs = t.ExpectedOutputs,
                    Verification = t.Verification,
                    Purpose = t.Purpose,
                    CreatedAt = t.CreatedAt,
                    StartedAt = t.StartedAt,
                    CompletedAt = t.CompletedAt,
                    PlanTaskId = t.PlanTaskId,
                    BlockedReason = t.BlockedReason,
                    Evidence = evidenceList
                };
                list[idx] = updated;
            }
        }

        if (updated != null)
        {
            await _messageStore.SaveAgentTodoAsync(updated);
        }
        return updated;
    }

    /// <summary>
    /// All TODOs for a session, oldest first. Lazily hydrates from the store on first access.
    /// </summary>
    public async Task<IReadOnlyList<AgentTodo>> GetSessionTodosAsync(string sessionId)
    {
        string key = sessionId ?? string.Empty;
        await EnsureLoadedAsync(key);
        lock (_lock)
        {
            return _bySession.GetOrAdd(key, _ => new List<AgentTodo>()).ToList();
        }
    }

    /// <summary>
    /// Hydrates a session's persisted TODOs into memory at most once per session per process.
    /// </summary>
    private async Task EnsureLoadedAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_lock)
        {
            if (!_loadedSessions.Add(sessionId)) return;
        }
        try
        {
            var rows = await _messageStore.GetSessionAgentTodosAsync(sessionId);
            lock (_lock)
            {
                var list = _bySession.GetOrAdd(sessionId, _ => new List<AgentTodo>());
                list.Clear();
                list.AddRange(rows);
            }
        }
        catch (Exception)
        {
            // Best-effort: a failed load leaves the session with an empty in-memory list.
        }
    }
}