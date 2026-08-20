using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// Deterministically resolves task step readiness and dependency propagation (Phase 3).
/// A step with unfulfilled dependencies stays Pending or transitions to Blocked.
/// Only steps whose dependencies are all Completed can become Ready.
/// </summary>
public static class StepDependencyResolver
{
    /// <summary>
    /// Evaluates step dependencies across a set of steps and returns an updated list with
    /// authoritative readiness statuses (Ready, Pending, Blocked, etc.).
    /// </summary>
    public static IReadOnlyList<TaskStep> ResolveStepStatuses(IReadOnlyList<TaskStep> steps)
    {
        if (steps == null || steps.Count == 0) return Array.Empty<TaskStep>();

        var stepMap = steps.ToDictionary(s => s.StepId, StringComparer.Ordinal);
        var resolved = new List<TaskStep>(steps.Count);

        foreach (var step in steps)
        {
            // Terminal or in-flight states are preserved
            if (step.Status is TaskStepStatus.Completed or TaskStepStatus.Skipped or TaskStepStatus.Running or TaskStepStatus.Verifying)
            {
                resolved.Add(step);
                continue;
            }

            // No dependencies declared: pending steps are ready for execution
            if (step.Dependencies == null || step.Dependencies.Count == 0)
            {
                if (step.Status == TaskStepStatus.Pending)
                {
                    resolved.Add(step with { Status = TaskStepStatus.Ready });
                }
                else
                {
                    resolved.Add(step);
                }
                continue;
            }

            bool anyDependencyFailedOrBlocked = false;
            bool allDependenciesCompleted = true;

            foreach (var depId in step.Dependencies)
            {
                if (!stepMap.TryGetValue(depId, out var dep))
                {
                    allDependenciesCompleted = false;
                    continue;
                }

                if (dep.Status is TaskStepStatus.Failed or TaskStepStatus.Blocked)
                {
                    anyDependencyFailedOrBlocked = true;
                    break;
                }

                if (dep.Status != TaskStepStatus.Completed)
                {
                    allDependenciesCompleted = false;
                }
            }

            if (anyDependencyFailedOrBlocked)
            {
                resolved.Add(step with
                {
                    Status = TaskStepStatus.Blocked,
                    FailureReason = "Blocked: one or more prerequisite steps failed or are blocked."
                });
            }
            else if (allDependenciesCompleted)
            {
                resolved.Add(step with { Status = TaskStepStatus.Ready });
            }
            else
            {
                resolved.Add(step with { Status = TaskStepStatus.Pending });
            }
        }

        return resolved;
    }
}
