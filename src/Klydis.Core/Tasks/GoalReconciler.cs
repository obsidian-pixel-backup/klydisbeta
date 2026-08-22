using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Klydis.Core.Tasks;

/// <summary>
/// Reconciles goals and work items after restarts or crashes.
/// Ensures stale executions are cleaned up and goals are resumed deterministically.
/// </summary>
public interface IGoalReconciler
{
    Task<bool> ReconcileGoalAsync(GoalEntity goal, IEnumerable<ExecutionLease>? activeLeases = null);
}

public class GoalReconciler : IGoalReconciler
{
    public Task<bool> ReconcileGoalAsync(GoalEntity goal, IEnumerable<ExecutionLease>? activeLeases = null)
    {
        if (goal == null) return Task.FromResult(false);

        var leaseMap = (activeLeases ?? Array.Empty<ExecutionLease>())
            .Where(l => l.GoalId == goal.Id)
            .ToDictionary(l => l.ExecutionId, StringComparer.OrdinalIgnoreCase);

        bool modified = false;

        // Inspect work items in Running state
        foreach (var item in goal.WorkItems.Where(w => w.State == WorkItemState.Running))
        {
            // If item has no active non-expired lease, it was interrupted by a crash/restart
            if (!leaseMap.TryGetValue(item.Id, out var lease) || lease.IsExpired)
            {
                item.State = WorkItemState.Pending;
                item.Attempts++;
                item.FailureReason = "Interrupted by system restart; scheduled for retry.";
                modified = true;
            }
        }

        // If goal was in Running or WaitingTool state but has runnable work, reset to Ready
        if (goal.State is GoalLifecycleState.Running or GoalLifecycleState.WaitingTool)
        {
            goal.State = GoalLifecycleState.Ready;
            modified = true;
        }

        return Task.FromResult(modified);
    }
}
