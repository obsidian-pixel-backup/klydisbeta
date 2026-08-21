using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// A model-generated execution plan governed by a runtime schema.
/// Contains an objective, dynamic task graph, and model-defined completion criteria.
/// </summary>
public sealed record ExecutionPlan
{
    public string Objective { get; init; } = string.Empty;
    public string? Strategy { get; init; }
    public IReadOnlyList<PlanTask> Tasks { get; init; } = Array.Empty<PlanTask>();
    public CompletionCriteria Completion { get; init; } = new();
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public ExecutionPlan() { }

    public ExecutionPlan(
        string objective,
        IReadOnlyList<PlanTask>? tasks = null,
        CompletionCriteria? completion = null,
        string? strategy = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        Objective = objective ?? string.Empty;
        Tasks = tasks ?? Array.Empty<PlanTask>();
        Completion = completion ?? new CompletionCriteria();
        Strategy = strategy;
        Metadata = metadata;
    }
}

/// <summary>
/// A single executable task unit inside an <see cref="ExecutionPlan"/>.
/// Contains identity, purpose, dependencies, required capabilities, outputs, and verification criteria.
/// </summary>
public sealed record PlanTask
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Purpose { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
    public VerificationCriteria Verification { get; init; } = new();
    public IReadOnlyList<string> Outputs { get; init; } = Array.Empty<string>();
    public TaskStepStatus Status { get; init; } = TaskStepStatus.Pending;

    public PlanTask() { }

    public PlanTask(
        string id,
        string description,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<string>? requiredCapabilities = null,
        VerificationCriteria? verification = null,
        IReadOnlyList<string>? outputs = null,
        TaskStepStatus status = TaskStepStatus.Pending,
        string? purpose = null,
        string? reason = null)
    {
        Id = id ?? string.Empty;
        Description = description ?? string.Empty;
        Dependencies = dependencies ?? Array.Empty<string>();
        RequiredCapabilities = requiredCapabilities ?? Array.Empty<string>();
        Verification = verification ?? new VerificationCriteria();
        Outputs = outputs ?? Array.Empty<string>();
        Status = status;
        Purpose = purpose;
        Reason = reason;
    }

    public bool IsOpen => Status is not (TaskStepStatus.Completed or TaskStepStatus.Skipped);
}

/// <summary>
/// Model-generated completion criteria for the overall objective.
/// An objective is complete only when all criteria are satisfied by execution evidence.
/// </summary>
public sealed record CompletionCriteria
{
    public IReadOnlyList<string> Conditions { get; init; } = Array.Empty<string>();
    public string? Description { get; init; }

    public CompletionCriteria() { }

    public CompletionCriteria(IReadOnlyList<string>? conditions, string? description = null)
    {
        Conditions = conditions ?? Array.Empty<string>();
        Description = description;
    }

    public bool IsEmpty => Conditions.Count == 0 && string.IsNullOrWhiteSpace(Description);
}

/// <summary>
/// Verification criteria for an individual task.
/// </summary>
public sealed record VerificationCriteria
{
    public IReadOnlyList<string> Criteria { get; init; } = Array.Empty<string>();
    public string? ExpectedEvidenceKind { get; init; }

    public VerificationCriteria() { }

    public VerificationCriteria(IReadOnlyList<string>? criteria, string? expectedEvidenceKind = null)
    {
        Criteria = criteria ?? Array.Empty<string>();
        ExpectedEvidenceKind = expectedEvidenceKind;
    }

    public bool IsEmpty => Criteria.Count == 0 && string.IsNullOrWhiteSpace(ExpectedEvidenceKind);
}

/// <summary>
/// An immutable revision of an execution plan.
/// Tracks evolutionary revisions triggered by observations during execution.
/// </summary>
public sealed record PlanRevision
{
    public int Revision { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public ExecutionPlan Plan { get; init; } = new();

    public PlanRevision() { }

    public PlanRevision(int revision, string reason, ExecutionPlan plan, DateTimeOffset? createdAt = null)
    {
        Revision = revision;
        Reason = reason ?? string.Empty;
        Plan = plan ?? new ExecutionPlan();
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Mutation operations supported on execution plans.
/// </summary>
public enum PlanPatchOperation
{
    AddTask,
    RemoveTask,
    ReplaceTask,
    UpdateTask,
    BlockTask,
    UnblockTask,
    ReorderTask,
    ChangeDependency,
    CompleteTask
}

/// <summary>
/// An incremental patch against the active execution plan.
/// Prevents the model from needing to regenerate entire plans on every observation.
/// </summary>
public sealed record PlanPatch
{
    public PlanPatchOperation Operation { get; init; }
    public string? TargetTaskId { get; init; }
    public string? AfterTaskId { get; init; }
    public PlanTask? Task { get; init; }
    public string? Reason { get; init; }
    public TaskStepStatus? StatusUpdate { get; init; }
    public IReadOnlyList<string>? UpdatedDependencies { get; init; }
    public string? UpdatedDescription { get; init; }

    public PlanPatch() { }

    public PlanPatch(
        PlanPatchOperation operation,
        string? targetTaskId = null,
        PlanTask? task = null,
        string? afterTaskId = null,
        string? reason = null,
        TaskStepStatus? statusUpdate = null,
        IReadOnlyList<string>? updatedDependencies = null,
        string? updatedDescription = null)
    {
        Operation = operation;
        TargetTaskId = targetTaskId;
        Task = task;
        AfterTaskId = afterTaskId;
        Reason = reason;
        StatusUpdate = statusUpdate;
        UpdatedDependencies = updatedDependencies;
        UpdatedDescription = updatedDescription;
    }
}

/// <summary>
/// Structured world state representation against which plans and tasks operate.
/// </summary>
public sealed record WorldState
{
    public IReadOnlyDictionary<string, object> Facts { get; init; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<WorldObservation> Observations { get; init; } = Array.Empty<WorldObservation>();
    public IReadOnlyList<WorldActionResult> Actions { get; init; } = Array.Empty<WorldActionResult>();

    public WorldState() { }

    public WorldState(
        IReadOnlyDictionary<string, object>? facts,
        IReadOnlyList<WorldObservation>? observations = null,
        IReadOnlyList<WorldActionResult>? actions = null)
    {
        Facts = facts ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        Observations = observations ?? Array.Empty<WorldObservation>();
        Actions = actions ?? Array.Empty<WorldActionResult>();
    }

    public static WorldState Empty { get; } = new();
}

/// <summary>
/// An observation recorded in the world state.
/// </summary>
public sealed record WorldObservation(
    string Source,
    string Summary,
    DateTimeOffset Timestamp,
    object? Data = null);

/// <summary>
/// An action result recorded in the world state.
/// </summary>
public sealed record WorldActionResult(
    string ActionId,
    string CapabilityOrTool,
    bool Success,
    string Output,
    DateTimeOffset Timestamp);
