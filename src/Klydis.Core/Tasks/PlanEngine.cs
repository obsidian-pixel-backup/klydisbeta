using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Result of applying a patch or compiling a plan in <see cref="PlanEngine"/>.
/// </summary>
public sealed record PlanEngineResult(
    bool Success,
    ExecutionPlan Plan,
    PlanRevision CurrentRevision,
    string? ErrorMessage = null);

/// <summary>
/// Execution compiler and lifecycle engine for model-generated <see cref="ExecutionPlan"/> instances.
/// Manages incremental plan patches, DAG dependency compilation, and immutable revision history.
/// </summary>
public sealed class PlanEngine
{
    private readonly object _lock = new();
    private readonly List<PlanRevision> _revisions = new();
    private ExecutionPlan _currentPlan;

    public string Objective => _currentPlan.Objective;
    public ExecutionPlan CurrentPlan => _currentPlan;
    public IReadOnlyList<PlanRevision> Revisions
    {
        get
        {
            lock (_lock)
            {
                return _revisions.ToList();
            }
        }
    }
    public int CurrentRevisionNumber => _revisions.Count;

    /// <summary>
    /// Creates a new PlanEngine initialized with an objective and zero tasks (or optional initial plan).
    /// </summary>
    public PlanEngine(string objective, ExecutionPlan? initialPlan = null)
    {
        _currentPlan = initialPlan ?? new ExecutionPlan(objective, Array.Empty<PlanTask>(), new CompletionCriteria());
        var initialRevision = new PlanRevision(1, "Initial plan created", _currentPlan, DateTimeOffset.UtcNow);
        _revisions.Add(initialRevision);
    }

    /// <summary>
    /// Creates a fresh execution plan from model generation, validating structure before replacing.
    /// </summary>
    public PlanEngineResult SetPlan(ExecutionPlan plan, string reason = "Model updated plan")
    {
        ArgumentNullException.ThrowIfNull(plan);

        var validation = PlanValidator.Validate(plan);
        if (!validation.IsValid)
        {
            return new PlanEngineResult(false, _currentPlan, _revisions.Last(), string.Join("; ", validation.Errors));
        }

        lock (_lock)
        {
            _currentPlan = plan;
            var revision = new PlanRevision(_revisions.Count + 1, reason, _currentPlan, DateTimeOffset.UtcNow);
            _revisions.Add(revision);
            return new PlanEngineResult(true, _currentPlan, revision);
        }
    }

    /// <summary>
    /// Applies an incremental <see cref="PlanPatch"/> against the active execution plan.
    /// </summary>
    public PlanEngineResult ApplyPatch(PlanPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        lock (_lock)
        {
            var currentTasks = _currentPlan.Tasks.ToList();
            string reason = patch.Reason ?? $"Applied patch {patch.Operation}";

            switch (patch.Operation)
            {
                case PlanPatchOperation.AddTask:
                    if (patch.Task == null)
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "AddTask requires a valid Task in the patch.");
                    }
                    if (string.IsNullOrWhiteSpace(patch.Task.Id))
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "Task must have a non-empty Id.");
                    }

                    if (!string.IsNullOrWhiteSpace(patch.AfterTaskId))
                    {
                        int idx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.AfterTaskId, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            currentTasks.Insert(idx + 1, patch.Task);
                        }
                        else
                        {
                            currentTasks.Add(patch.Task);
                        }
                    }
                    else
                    {
                        currentTasks.Add(patch.Task);
                    }
                    break;

                case PlanPatchOperation.RemoveTask:
                    if (string.IsNullOrWhiteSpace(patch.TargetTaskId))
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "RemoveTask requires TargetTaskId.");
                    }
                    currentTasks.RemoveAll(t => string.Equals(t.Id, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase));
                    // Remove references to this task in other tasks' dependencies
                    for (int i = 0; i < currentTasks.Count; i++)
                    {
                        if (currentTasks[i].Dependencies.Contains(patch.TargetTaskId, StringComparer.OrdinalIgnoreCase))
                        {
                            var updatedDeps = currentTasks[i].Dependencies
                                .Where(d => !string.Equals(d, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            currentTasks[i] = currentTasks[i] with { Dependencies = updatedDeps };
                        }
                    }
                    break;

                case PlanPatchOperation.ReplaceTask:
                    if (string.IsNullOrWhiteSpace(patch.TargetTaskId) || patch.Task == null)
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "ReplaceTask requires TargetTaskId and replacement Task.");
                    }
                    int replaceIdx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase));
                    if (replaceIdx >= 0)
                    {
                        currentTasks[replaceIdx] = patch.Task;
                    }
                    else
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), $"Task '{patch.TargetTaskId}' not found.");
                    }
                    break;

                case PlanPatchOperation.UpdateTask:
                    if (string.IsNullOrWhiteSpace(patch.TargetTaskId))
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "UpdateTask requires TargetTaskId.");
                    }
                    int updateIdx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase));
                    if (updateIdx >= 0)
                    {
                        var t = currentTasks[updateIdx];
                        currentTasks[updateIdx] = t with
                        {
                            Description = patch.UpdatedDescription ?? patch.Task?.Description ?? t.Description,
                            Status = patch.StatusUpdate ?? patch.Task?.Status ?? t.Status,
                            Dependencies = patch.UpdatedDependencies ?? patch.Task?.Dependencies ?? t.Dependencies,
                            RequiredCapabilities = patch.Task?.RequiredCapabilities ?? t.RequiredCapabilities,
                            Verification = patch.Task?.Verification ?? t.Verification,
                            Outputs = patch.Task?.Outputs ?? t.Outputs,
                            Purpose = patch.Task?.Purpose ?? t.Purpose,
                            Reason = patch.Reason ?? t.Reason
                        };
                    }
                    else
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), $"Task '{patch.TargetTaskId}' not found.");
                    }
                    break;

                case PlanPatchOperation.CompleteTask:
                    if (string.IsNullOrWhiteSpace(patch.TargetTaskId))
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "CompleteTask requires TargetTaskId.");
                    }
                    int compIdx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase));
                    if (compIdx >= 0)
                    {
                        currentTasks[compIdx] = currentTasks[compIdx] with { Status = TaskStepStatus.Completed };
                    }
                    else
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), $"Task '{patch.TargetTaskId}' not found.");
                    }
                    break;

                case PlanPatchOperation.BlockTask:
                    if (string.IsNullOrWhiteSpace(patch.TargetTaskId))
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "BlockTask requires TargetTaskId.");
                    }
                    int blockIdx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase));
                    if (blockIdx >= 0)
                    {
                        currentTasks[blockIdx] = currentTasks[blockIdx] with { Status = TaskStepStatus.Blocked };
                    }
                    break;

                case PlanPatchOperation.UnblockTask:
                    if (string.IsNullOrWhiteSpace(patch.TargetTaskId))
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "UnblockTask requires TargetTaskId.");
                    }
                    int unblockIdx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase));
                    if (unblockIdx >= 0)
                    {
                        currentTasks[unblockIdx] = currentTasks[unblockIdx] with { Status = TaskStepStatus.Pending };
                    }
                    break;

                case PlanPatchOperation.ChangeDependency:
                    if (string.IsNullOrWhiteSpace(patch.TargetTaskId) || patch.UpdatedDependencies == null)
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "ChangeDependency requires TargetTaskId and UpdatedDependencies.");
                    }
                    int depIdx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase));
                    if (depIdx >= 0)
                    {
                        currentTasks[depIdx] = currentTasks[depIdx] with { Dependencies = patch.UpdatedDependencies };
                    }
                    break;

                case PlanPatchOperation.ReorderTask:
                    if (string.IsNullOrWhiteSpace(patch.TargetTaskId) || string.IsNullOrWhiteSpace(patch.AfterTaskId))
                    {
                        return new PlanEngineResult(false, _currentPlan, _revisions.Last(), "ReorderTask requires TargetTaskId and AfterTaskId.");
                    }
                    int sourceIdx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.TargetTaskId, StringComparison.OrdinalIgnoreCase));
                    if (sourceIdx >= 0)
                    {
                        var taskToMove = currentTasks[sourceIdx];
                        currentTasks.RemoveAt(sourceIdx);
                        int destIdx = currentTasks.FindIndex(t => string.Equals(t.Id, patch.AfterTaskId, StringComparison.OrdinalIgnoreCase));
                        if (destIdx >= 0)
                        {
                            currentTasks.Insert(destIdx + 1, taskToMove);
                        }
                        else
                        {
                            currentTasks.Add(taskToMove);
                        }
                    }
                    break;
            }

            var updatedPlan = _currentPlan with { Tasks = currentTasks };
            var validation = PlanValidator.Validate(updatedPlan);
            if (!validation.IsValid)
            {
                return new PlanEngineResult(false, _currentPlan, _revisions.Last(), string.Join("; ", validation.Errors));
            }

            _currentPlan = updatedPlan;
            var newRevision = new PlanRevision(_revisions.Count + 1, reason, _currentPlan, DateTimeOffset.UtcNow);
            _revisions.Add(newRevision);
            return new PlanEngineResult(true, _currentPlan, newRevision);
        }
    }

    /// <summary>
    /// Resolves task execution states based on DAG dependencies:
    /// Tasks whose dependencies are all Completed transition from Pending to Ready.
    /// </summary>
    public IReadOnlyList<PlanTask> ResolveExecutionDag()
    {
        lock (_lock)
        {
            var tasks = _currentPlan.Tasks.ToList();
            var taskMap = tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i];
                if (task.Status == TaskStepStatus.Completed || task.Status == TaskStepStatus.Skipped)
                {
                    continue;
                }

                bool allDepsComplete = true;
                bool hasBlockedOrFailedDep = false;

                foreach (var depId in task.Dependencies)
                {
                    if (taskMap.TryGetValue(depId, out var depTask))
                    {
                        if (depTask.Status is TaskStepStatus.Failed or TaskStepStatus.Blocked)
                        {
                            hasBlockedOrFailedDep = true;
                        }
                        if (depTask.Status != TaskStepStatus.Completed)
                        {
                            allDepsComplete = false;
                        }
                    }
                }

                if (hasBlockedOrFailedDep && task.Status != TaskStepStatus.Blocked)
                {
                    tasks[i] = task with { Status = TaskStepStatus.Blocked };
                }
                else if (allDepsComplete && task.Status == TaskStepStatus.Pending)
                {
                    tasks[i] = task with { Status = TaskStepStatus.Ready };
                }
                else if (!allDepsComplete && task.Status == TaskStepStatus.Ready)
                {
                    tasks[i] = task with { Status = TaskStepStatus.Pending };
                }
            }

            _currentPlan = _currentPlan with { Tasks = tasks };
            return tasks;
        }
    }

    /// <summary>
    /// Gets the next executable task in the DAG (first Ready or running task).
    /// </summary>
    public PlanTask? GetNextExecutableTask()
    {
        var resolved = ResolveExecutionDag();
        return resolved.FirstOrDefault(t => t.Status == TaskStepStatus.Running)
            ?? resolved.FirstOrDefault(t => t.Status == TaskStepStatus.Ready);
    }

    /// <summary>
    /// Projects the execution plan tasks to <see cref="ToolExecutor.PlanEntry"/> items for UI and backwards-compatibility.
    /// </summary>
    public IReadOnlyList<ToolExecutor.PlanEntry> ProjectToPlanEntries()
    {
        lock (_lock)
        {
            return _currentPlan.Tasks.Select(t => new ToolExecutor.PlanEntry(
                t.Description,
                t.Status is TaskStepStatus.Completed or TaskStepStatus.Skipped)).ToList();
        }
    }

    /// <summary>
    /// Compiles the plan tasks into first-class <see cref="TaskStep"/> records.
    /// </summary>
    public IReadOnlyList<TaskStep> CompileToTaskSteps(string? taskId = null)
    {
        lock (_lock)
        {
            var steps = new List<TaskStep>(_currentPlan.Tasks.Count);
            for (int i = 0; i < _currentPlan.Tasks.Count; i++)
            {
                var pt = _currentPlan.Tasks[i];
                var resolution = CapabilityResolver.Resolve(pt);

                steps.Add(new TaskStep(
                    StepId: string.IsNullOrWhiteSpace(pt.Id) ? TaskStep.BuildStepId(taskId, i) : pt.Id,
                    TaskId: taskId,
                    Order: i,
                    Title: pt.Description,
                    Status: pt.Status,
                    ExpectedActionKind: StepActionKind.FileMutation, // default unless overridden
                    AllowedTools: resolution.AllowedToolNames,
                    RequiredSkills: Array.Empty<string>(),
                    ExpectedArtifacts: pt.Outputs,
                    VerificationCriteria: pt.Verification.Criteria,
                    CompletionCondition: null,
                    AttemptCount: 0,
                    LastActionId: null,
                    StartedAt: null,
                    CompletedAt: pt.Status == TaskStepStatus.Completed ? DateTime.UtcNow : null,
                    Dependencies: pt.Dependencies));
            }
            return steps;
        }
    }
}
