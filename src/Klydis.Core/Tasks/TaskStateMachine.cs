using System;
using System.Collections.Generic;
using Klydis.Core.Chat;
using TaskStatus = Klydis.Core.Chat.TaskStatus;

namespace Klydis.Core.Tasks;

/// <summary>
/// Lifecycle of a single plan step. A step is a logical unit of work inside a task; the
/// plan's open/checked items are the current representation, and these states are what a
/// first-class TaskStep record will carry once steps become their own durable records.
/// </summary>
public enum TaskStepStatus
{
    Pending,
    Ready,
    Running,
    Blocked,
    Verifying,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// Status of one continuous execution attempt of a task. A task may be executed by many
/// runs over its lifetime (each run = one attempt); a run contains the turns, which contain
/// the generations and tool executions.
/// </summary>
public enum RunStatus
{
    /// <summary>The run is the task's CURRENT continuous execution attempt; it stays open
    /// across user turns until the task completes, fails, is suspended, or is interrupted.</summary>
    Running,

    /// <summary>Paused by the harness (awaiting user input / external condition).</summary>
    Paused,

    /// <summary>The task was sealed Completed by the supervisor within this run.</summary>
    Completed,

    /// <summary>The run ended in a task failure.</summary>
    Failed,

    /// <summary>Explicitly cancelled (user stop / model switch / teardown).</summary>
    Cancelled,

    /// <summary>The run was set aside while the task remains resumable — e.g. a NEW task
    /// replaced this one, or the turn ended without a terminal outcome. NOT cancellation:
    /// the task is still active and a later turn reopens/continues execution.</summary>
    Suspended,

    /// <summary>The run was cut short mid-turn (generation cancelled, app stop, stall
    /// watchdog) but the task itself is still active and resumable.</summary>
    Interrupted,

    /// <summary>The run is waiting on the user before it can proceed.</summary>
    AwaitingUser
}

/// <summary>
/// One continuous execution attempt of a task. Durable identity for the Task → Run → Step →
/// Turn → Generation hierarchy: the run owns the turn counters and the terminal state.
/// Persisted in the <c>runs</c> table so a restart can answer "which run was executing, and
/// how far did it get?" — the checkpoint/recovery phase builds on this.
/// </summary>
public sealed record TaskRun(
    string RunId,
    string TaskId,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    RunStatus Status,
    int TurnCount);

/// <summary>
/// How a single model generation ended. This is a FACT about the generation, never a claim
/// about the task: a generation ending (EOS, max tokens, tool call, error, cancellation) has
/// zero authority over whether a turn, step, run, or task ended. The supervisor maps this to
/// an <see cref="ExecutionDecision"/>.
/// </summary>
public enum GenerationOutcome
{
    /// <summary>Generation completed normally with visible output.</summary>
    CompletedTurn,

    /// <summary>The generation produced a parseable tool call (execution continues with the tool).</summary>
    ToolCallProduced,

    /// <summary>In Autonomous mode the generation produced ONLY natural language — no tool call,
    /// no completion claim, no replan. This is a protocol failure, NOT a completed turn: the
    /// model may understand the request but refuse to enter the tool protocol (the observed
    /// "Good morning! Please tell me what you want next" pattern).</summary>
    NoActionProduced,

    /// <summary>The generation hit the output token cap — the harness ended the stream.</summary>
    OutputBudgetExhausted,

    /// <summary>The prompt filled the context window (empty generation, structural failure).</summary>
    ContextExhausted,

    /// <summary>The native decode failed or overflowed mid-stream after tokens were emitted.</summary>
    GenerationCutShort,

    /// <summary>The model emitted its own end-of-turn token mid-sentence (declining to continue).</summary>
    ModelEndedEarly,

    /// <summary>The generation was cancelled (user stop, model switch, teardown).</summary>
    Cancelled,

    /// <summary>The model produced no usable output (empty/degenerate loop).</summary>
    DegenerateLoop,

    /// <summary>An error terminated the generation.</summary>
    Error
}

/// <summary>
/// What the harness decides should happen next after a generation. The model produces output;
/// the supervisor produces this. The model can never directly choose these outcomes.
/// </summary>
public enum ExecutionDecision
{
    /// <summary>Invoke the model again for the same step (e.g. a continuation).</summary>
    ContinueGeneration,

    /// <summary>Execute the parsed tool call.</summary>
    ExecuteTool,

    /// <summary>Continue work on the current step (tool results processed, loop resumes).</summary>
    ContinueStep,

    /// <summary>The plan no longer reflects reality — revise it before continuing.</summary>
    Replan,

    /// <summary>Autonomous mode produced no valid action — inject a COMPACT action-required
    /// repair instruction and regenerate (bounded by the protocol-repair budget).</summary>
    RepairProtocol,

    /// <summary>Run the verification gates (completion was claimed or steps are done).</summary>
    Verify,

    /// <summary>Harness-verified completion — seal the task.</summary>
    CompleteTask,

    /// <summary>Pause execution pending user input or an external condition.</summary>
    Pause,

    /// <summary>Halt the task as failed.</summary>
    FailTask,

    /// <summary>Wait for the user (cancellation, clarification, queued steer).</summary>
    AwaitUser
}

/// <summary>
/// The supervisor's authoritative answer after evaluating a generation outcome against the
/// durable task state: what to do next, why, and which step to work.
/// </summary>
public readonly record struct SupervisorDecision(
    ExecutionDecision Decision,
    ContinuationReason Reason,
    string? NextStepId = null);

/// <summary>
/// Enforces legal state transitions for tasks and steps. The invariant: no state change may
/// happen without going through here, so a model generation ending (or a model claiming
/// completion) can never by itself move a task to a terminal state — the harness must approve
/// every transition.
/// </summary>
public static class TaskStateMachine
{
    /// <summary>
    /// Legal task transitions. Terminal states (Completed, Failed, Cancelled) may only be
    /// re-entered via an explicit reopen (Completed/Failed → Running), never automatically.
    /// </summary>
    public static bool CanTransition(TaskStatus from, TaskStatus to)
    {
        if (from == to) return true;
        return (from, to) switch
        {
            (TaskStatus.Pending, TaskStatus.Planning or TaskStatus.Cancelled or TaskStatus.Running) => true,
            (TaskStatus.Planning, TaskStatus.Ready or TaskStatus.Failed or TaskStatus.Cancelled or TaskStatus.Running) => true,
            (TaskStatus.Ready, TaskStatus.Running or TaskStatus.Cancelled or TaskStatus.Failed) => true,
            (TaskStatus.Running, TaskStatus.Verifying or TaskStatus.Waiting or TaskStatus.Blocked or TaskStatus.AwaitingUser or TaskStatus.Paused or TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled) => true,
            (TaskStatus.Waiting, TaskStatus.Running or TaskStatus.Cancelled or TaskStatus.Blocked) => true,
            (TaskStatus.Verifying, TaskStatus.Running or TaskStatus.Completed or TaskStatus.Failed) => true,
            (TaskStatus.Blocked, TaskStatus.Running or TaskStatus.Failed or TaskStatus.Cancelled) => true,
            (TaskStatus.AwaitingUser, TaskStatus.Running or TaskStatus.Cancelled) => true,
            (TaskStatus.Paused, TaskStatus.Running or TaskStatus.Cancelled) => true,
            // Explicit reopen after a terminal state (task resumed with its plan restored).
            (TaskStatus.Completed or TaskStatus.Failed, TaskStatus.Running) => true,
            _ => false
        };
    }

    /// <summary>
    /// Performs a guarded transition, returning the new task or null when illegal.
    /// </summary>
    public static AgentTask? TryTransition(AgentTask task, TaskStatus to)
    {
        if (!CanTransition(task.Status, to)) return null;
        return task with { Status = to, UpdatedAtUtc = DateTime.UtcNow };
    }

    /// <summary>
    /// Legal step transitions (Pending → Ready → Running → Verifying → Completed, with the
    /// failure/branch states).
    /// </summary>
    public static bool CanStepTransition(TaskStepStatus from, TaskStepStatus to)
    {
        if (from == to) return true;
        return (from, to) switch
        {
            (TaskStepStatus.Pending, TaskStepStatus.Ready or TaskStepStatus.Skipped or TaskStepStatus.Blocked or TaskStepStatus.Failed) => true,
            (TaskStepStatus.Ready, TaskStepStatus.Running or TaskStepStatus.Skipped or TaskStepStatus.Blocked or TaskStepStatus.Failed) => true,
            (TaskStepStatus.Running, TaskStepStatus.Verifying or TaskStepStatus.Blocked or TaskStepStatus.Failed or TaskStepStatus.Completed) => true,
            (TaskStepStatus.Verifying, TaskStepStatus.Completed or TaskStepStatus.Failed or TaskStepStatus.Running) => true,
            (TaskStepStatus.Blocked, TaskStepStatus.Ready or TaskStepStatus.Failed or TaskStepStatus.Skipped) => true,
            (TaskStepStatus.Failed, TaskStepStatus.Ready or TaskStepStatus.Running) => true,
            _ => false
        };
    }

    /// <summary>
    /// The canonical happy path for documentation and supervisor decisions.
    /// </summary>
    public static readonly IReadOnlyList<TaskStepStatus> HappyPath = new[]
    {
        TaskStepStatus.Pending, TaskStepStatus.Ready, TaskStepStatus.Running,
        TaskStepStatus.Verifying, TaskStepStatus.Completed
    };
}
