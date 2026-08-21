using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Capabilities;

namespace Klydis.Core.Tasks;

/// <summary>
/// Result of plan schema and structural validation.
/// </summary>
public sealed record PlanValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public static PlanValidationResult Success(IReadOnlyList<string>? warnings = null)
        => new(true, Array.Empty<string>(), warnings ?? Array.Empty<string>());

    public static PlanValidationResult Failure(IReadOnlyList<string> errors, IReadOnlyList<string>? warnings = null)
        => new(false, errors, warnings ?? Array.Empty<string>());

    public static PlanValidationResult Failure(string error)
        => new(false, new[] { error }, Array.Empty<string>());
}

/// <summary>
/// Enforces structural correctness and runtime schema invariants on <see cref="ExecutionPlan"/> instances.
/// Validates task IDs, DAG dependency graphs (cycle detection), capability references, and completion criteria.
/// Note: The validator never dictates what the tasks should be — it only validates structure.
/// </summary>
public static class PlanValidator
{
    /// <summary>
    /// Validates an <see cref="ExecutionPlan"/> for structural and graph correctness.
    /// </summary>
    public static PlanValidationResult Validate(
        ExecutionPlan? plan,
        ICapabilityRegistry? capabilityRegistry = null)
    {
        if (plan == null)
        {
            return PlanValidationResult.Failure("Plan cannot be null.");
        }

        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.Objective))
        {
            errors.Add("Plan objective must not be empty.");
        }

        if (plan.Tasks == null)
        {
            errors.Add("Plan tasks collection cannot be null.");
            return PlanValidationResult.Failure(errors, warnings);
        }

        var taskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taskMap = new Dictionary<string, PlanTask>(StringComparer.OrdinalIgnoreCase);

        // 1. Task ID uniqueness & non-emptiness
        for (int i = 0; i < plan.Tasks.Count; i++)
        {
            var task = plan.Tasks[i];
            if (task == null)
            {
                errors.Add($"Task at index {i} is null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(task.Id))
            {
                errors.Add($"Task at index {i} has an empty or missing ID.");
                continue;
            }

            if (!taskIds.Add(task.Id))
            {
                errors.Add($"Duplicate task ID '{task.Id}' found in plan.");
            }
            else
            {
                taskMap[task.Id] = task;
            }

            if (string.IsNullOrWhiteSpace(task.Description))
            {
                errors.Add($"Task '{task.Id}' has an empty description.");
            }

            // Self-dependency check
            if (task.Dependencies != null && task.Dependencies.Any(d => string.Equals(d, task.Id, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Task '{task.Id}' cannot depend on itself.");
            }
        }

        // 2. Dependency existence checks
        foreach (var task in plan.Tasks.Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id)))
        {
            if (task.Dependencies == null) continue;

            foreach (var depId in task.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(depId)) continue;

                if (!taskMap.ContainsKey(depId))
                {
                    errors.Add($"Task '{task.Id}' specifies unknown dependency '{depId}'.");
                }
            }
        }

        // 3. DAG cycle detection (DFS with recursion stack)
        if (errors.Count == 0 && plan.Tasks.Count > 1)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cyclePath = new List<string>();

            bool HasCycle(string currentId)
            {
                visited.Add(currentId);
                inStack.Add(currentId);
                cyclePath.Add(currentId);

                if (taskMap.TryGetValue(currentId, out var currentTask) && currentTask.Dependencies != null)
                {
                    foreach (var dep in currentTask.Dependencies)
                    {
                        if (string.IsNullOrWhiteSpace(dep) || !taskMap.ContainsKey(dep)) continue;

                        if (!visited.Contains(dep))
                        {
                            if (HasCycle(dep)) return true;
                        }
                        else if (inStack.Contains(dep))
                        {
                            cyclePath.Add(dep);
                            return true;
                        }
                    }
                }

                inStack.Remove(currentId);
                cyclePath.RemoveAt(cyclePath.Count - 1);
                return false;
            }

            foreach (var taskId in taskMap.Keys)
            {
                if (!visited.Contains(taskId))
                {
                    if (HasCycle(taskId))
                    {
                        errors.Add($"Dependency cycle detected: {string.Join(" -> ", cyclePath)}");
                        break;
                    }
                }
            }
        }

        // 4. Capability validation (if registry provided)
        if (capabilityRegistry != null)
        {
            var registeredCaps = new HashSet<string>(
                capabilityRegistry.GetAll().Select(c => c.Id),
                StringComparer.OrdinalIgnoreCase);

            foreach (var task in plan.Tasks.Where(t => t != null))
            {
                if (task.RequiredCapabilities == null) continue;

                foreach (var cap in task.RequiredCapabilities)
                {
                    if (string.IsNullOrWhiteSpace(cap)) continue;

                    if (!registeredCaps.Contains(cap))
                    {
                        warnings.Add($"Task '{task.Id}' specifies capability '{cap}' which is not currently registered.");
                    }
                }
            }
        }

        // 5. Completion criteria presence
        if (plan.Tasks.Count > 0 && (plan.Completion == null || plan.Completion.IsEmpty))
        {
            warnings.Add("Plan contains tasks but no model-generated completion criteria were specified.");
        }

        return errors.Count == 0
            ? PlanValidationResult.Success(warnings)
            : PlanValidationResult.Failure(errors, warnings);
    }
}
