using System;
using System.Collections.Generic;

namespace Klydis.Core.Tasks;

/// <summary>
/// Execution states for an individual work item within a goal.
/// </summary>
public enum WorkItemState
{
    Pending,
    Running,
    Completed,
    Failed,
    Blocked
}

/// <summary>
/// Represents a precondition or completion condition for a work item.
/// </summary>
public sealed record WorkItemCondition(
    string Description,
    Func<WorkItem, bool>? Evaluator = null);

/// <summary>
/// A first-class tracked unit of work within a goal.
/// The runtime owns and enforces execution state; the model can propose items and mutate plans.
/// </summary>
public sealed class WorkItem
{
    public required string Id { get; init; }
    public required string GoalId { get; init; }
    public required string Objective { get; init; }
    public WorkItemState State { get; set; } = WorkItemState.Pending;
    public int Attempts { get; set; } = 0;
    public List<string> Dependencies { get; init; } = new();
    public List<WorkItemCondition> Preconditions { get; init; } = new();
    public List<WorkItemCondition> CompletionConditions { get; init; } = new();
    public string? ResultArtifactId { get; set; }
    public string? FailureReason { get; set; }
    public List<string> EvidenceIds { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsRunnable(IReadOnlyDictionary<string, WorkItem> allItems)
    {
        if (State != WorkItemState.Pending && State != WorkItemState.Failed)
            return false;

        foreach (var depId in Dependencies)
        {
            if (allItems.TryGetValue(depId, out var dep) && dep.State != WorkItemState.Completed)
            {
                return false;
            }
        }

        return true;
    }
}
