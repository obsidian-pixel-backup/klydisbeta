using System;
using Klydis.Core.Chat;
using TaskStatus = Klydis.Core.Chat.TaskStatus;

namespace Klydis.Core.Tasks;

/// <summary>
/// Classification of a user message against the session's current task. Decided by the
/// HARNESS before the model runs — never by the model. This is the durable answer to
/// "is this message a new task or a continuation?" that the plan, the queue, and the
/// completion gate all key off.
/// </summary>
public enum TaskResolutionKind
{
    /// <summary>
    /// No prior task, or the message clearly starts a fresh piece of work. A new task is
    /// created; the previous task's plan/queue context stops being presented as current.
    /// </summary>
    NewTask,

    /// <summary>
    /// The message continues the current task without changing its nature ("keep going",
    /// "do the next step", a plain follow-up with no explicit relationship language).
    /// </summary>
    ContinueTask,

    /// <summary>
    /// The message modifies the current task in place ("also add X", "use SQLite instead",
    /// "change the color to blue"). Same task, same plan, same queue.
    /// </summary>
    SteerTask,

    /// <summary>
    /// The current task was completed but the message clearly returns to it ("continue
    /// task A", strong objective overlap). The task is reopened (Running) with its plan
    /// restored.
    /// </summary>
    ReopenTask
}

/// <summary>
/// Durable execution task. This is the unit of agentic work: a user objective, its plan,
/// and the queue/artifact/evidence state attached to it. Distinct from a session (a
/// conversation can contain many tasks) and from a message (many messages can belong to
/// one task). Persisted in the <c>tasks</c> table so tasks survive restarts and remain
/// resumable even after being superseded by a newer task in the same session.
/// </summary>
public sealed record AgentTask(
    string TaskId,
    string SessionId,
    string Objective,
    TaskStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? PlanJson = null,
    string? Summary = null,
    bool RequiresExecution = true)
{
    /// <summary>
    /// Creates a fresh task with a new id and a Running status.
    /// </summary>
    public static AgentTask Create(string sessionId, string objective, bool requiresExecution = true)
        => new(
            TaskId: "T-" + Guid.NewGuid().ToString("N")[..12],
            SessionId: sessionId,
            Objective: objective,
            Status: TaskStatus.Running,
            CreatedAtUtc: DateTime.UtcNow,
            UpdatedAtUtc: DateTime.UtcNow,
            RequiresExecution: requiresExecution);
}
