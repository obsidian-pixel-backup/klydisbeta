using System;
using System.Collections.Generic;

namespace Klydis.Core.Tasks;

/// <summary>
/// A first-class model-generated TODO item. TODOs are the atomic units of pending,
/// active, and completed work. Unlike plans (which describe intent), TODOs track
/// specific units of work with evidence, verification, and lifecycle timestamps.
/// </summary>
public sealed record AgentTodo
{
    public string Id { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public TodoStatus Status { get; init; } = TodoStatus.Pending;
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RelatedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpectedOutputs { get; init; } = Array.Empty<string>();
    public string? Verification { get; init; }
    public string? Purpose { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? PlanTaskId { get; init; }
    public string? BlockedReason { get; init; }
    public IReadOnlyList<TodoEvidence> Evidence { get; init; } = Array.Empty<TodoEvidence>();

    public bool IsOpen => Status is not (TodoStatus.Completed or TodoStatus.Skipped or TodoStatus.Cancelled);
}

/// <summary>
/// Lifecycle status of a model-generated TODO item.
/// </summary>
public enum TodoStatus
{
    Pending,
    Ready,
    Running,
    Completed,
    Blocked,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>
/// A piece of execution evidence linked to a TODO item.
/// Evidence is accumulated automatically as the agent executes tools.
/// </summary>
public sealed record TodoEvidence
{
    public string Id { get; init; } = string.Empty;
    public string TodoId { get; init; } = string.Empty;
    public EvidenceKind Kind { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public bool Passed { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
