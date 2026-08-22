using System;
using System.Collections.Generic;

namespace Klydis.Core.Tasks;

/// <summary>
/// A durable structured state snapshot that preserves goals, plans, work items, observations,
/// evidence, and budget state across context compaction and restarts.
/// </summary>
public sealed record AgentStateSnapshot
{
    public required string GoalId { get; init; }
    public required string Objective { get; init; }
    public GoalLifecycleState State { get; init; }
    public IReadOnlyList<string> Plan { get; init; } = Array.Empty<string>();
    public IReadOnlyList<WorkItem> WorkItems { get; init; } = Array.Empty<WorkItem>();
    public IReadOnlyList<string> CompletedItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FailedItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KeyObservations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Decisions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Artifacts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Evidence> Evidence { get; init; } = Array.Empty<Evidence>();
    public IReadOnlyList<string> OpenQuestions { get; init; } = Array.Empty<string>();
    public string? CurrentStrategy { get; init; }
    public BudgetSnapshot? Budget { get; init; }
    public string? LastAction { get; init; }
    public string? NextRequiredAction { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
