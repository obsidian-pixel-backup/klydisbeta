using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Chat;
using Klydis.Core.Memory;
using Klydis.Core.Tracing;

namespace Klydis.Core.Tasks;

/// <summary>
/// Explicit lifecycle states for an autonomous run.
/// </summary>
public enum AutonomousRunState
{
    Created,
    Initializing,
    Running,
    WaitingForModel,
    ExecutingTool,
    Observing,
    Recovering,
    Verifying,
    Stalled,
    Completed,
    Failed,
    Cancelled,
    TimedOut
}

/// <summary>
/// Progress evaluation verdict for an execution cycle.
/// </summary>
public sealed record ProgressEvaluation(
    bool ProgressMade,
    string Reason,
    int ConsecutiveNoProgressCount);

/// <summary>
/// Context representation of an active autonomous run.
/// </summary>
public sealed class AutonomousRunContext
{
    public string RunId { get; init; } = $"R-{Guid.NewGuid():N}"[..14];
    public string TaskId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAtUtc { get; set; }

    public AutonomousRunState State { get; set; } = AutonomousRunState.Created;
    public int TurnCount { get; set; } = 0;
    public int IterationCount { get; set; } = 0;
    public int ConsecutiveNoProgressCount { get; set; } = 0;
    public int ConsecutiveRejectionsCount { get; set; } = 0;

    public string? LastTool { get; set; }
    public string? LastToolArguments { get; set; }
    public string? LastResultSummary { get; set; }
    public string? LastFileHash { get; set; }
    public string? LastTerminalResult { get; set; }
    public string? LastObjectiveEvidence { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }

    public bool IsTerminal => State is AutonomousRunState.Completed
        or AutonomousRunState.Failed
        or AutonomousRunState.Cancelled
        or AutonomousRunState.TimedOut;

    public bool IsAutonomous { get; init; } = true;

    public void RecordToolActivity(string toolName, bool success, string? summary = null)
    {
        LastTool = toolName;
        LastResultSummary = summary ?? (success ? "Success" : "Failed");
    }
}

/// <summary>
/// Authoritative controller for autonomous task runs.
/// Owns the lifecycle, progress evaluation, auto-continuation, and recovery mechanics.
/// </summary>
public sealed class AutonomousRunController
{
    private readonly IExecutionEventStore? _eventStore;
    private readonly ILogger<AutonomousRunController>? _logger;

    public const int DefaultMaxIterations = 1000;
    public const int DefaultMaxNoProgressIterations = 3;
    public const int DefaultMaxRepeatedFailures = 3;

    public AutonomousRunController(
        IExecutionEventStore? eventStore = null,
        ILogger<AutonomousRunController>? logger = null)
    {
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Explicitly transitions the run to a new state if not in a terminal state.
    /// </summary>
    public bool TransitionState(AutonomousRunContext run, AutonomousRunState newState)
    {
        if (run.IsTerminal) return false;
        run.State = newState;
        return true;
    }

    /// <summary>
    /// Creates and initializes a new autonomous run context.
    /// </summary>
    public AutonomousRunContext CreateRun(string taskId, string sessionId, string objective)
    {
        var run = new AutonomousRunContext
        {
            TaskId = taskId,
            SessionId = sessionId,
            Objective = objective,
            State = AutonomousRunState.Initializing
        };

        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = sessionId,
            TaskId = taskId,
            RunId = run.RunId,
            Category = ExecutionEventCategory.TaskStarted,
            Title = "Autonomous run started",
            Summary = objective
        });

        run.State = AutonomousRunState.Running;
        return run;
    }

    /// <summary>
    /// Evaluates if meaningful forward progress was made in the previous cycle.
    /// </summary>
    public ProgressEvaluation EvaluateProgress(
        AutonomousRunContext run,
        bool toolExecuted,
        bool toolSucceeded,
        int filesChanged,
        int artifactsCreated,
        bool terminalExecuted,
        bool evidenceObtained,
        bool stateAdvanced)
    {
        bool progressMade = (toolExecuted && toolSucceeded) ||
                            filesChanged > 0 ||
                            artifactsCreated > 0 ||
                            terminalExecuted ||
                            evidenceObtained ||
                            stateAdvanced;

        if (progressMade)
        {
            run.ConsecutiveNoProgressCount = 0;
            if (run.State == AutonomousRunState.Stalled || run.State == AutonomousRunState.Recovering)
            {
                run.State = AutonomousRunState.Running;
            }
            return new ProgressEvaluation(true, "Measurable state transition achieved", 0);
        }

        run.ConsecutiveNoProgressCount++;
        if (run.ConsecutiveNoProgressCount >= DefaultMaxNoProgressIterations)
        {
            run.State = AutonomousRunState.Stalled;
            _eventStore?.RecordEvent(new ExecutionEvent
            {
                SessionId = run.SessionId,
                TaskId = run.TaskId,
                RunId = run.RunId,
                Category = ExecutionEventCategory.Stalled,
                Title = "Autonomous run stalled",
                Summary = $"No progress made for {run.ConsecutiveNoProgressCount} iterations"
            });
        }

        return new ProgressEvaluation(
            false,
            $"No state change in cycle ({run.ConsecutiveNoProgressCount}/{DefaultMaxNoProgressIterations})",
            run.ConsecutiveNoProgressCount);
    }

    /// <summary>
    /// Builds the minimal automatic recovery prompt when no executable action occurred.
    /// </summary>
    public string BuildRecoveryPrompt(AutonomousRunContext run, string? specificHint = null)
    {
        run.State = AutonomousRunState.Recovering;
        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = run.SessionId,
            TaskId = run.TaskId,
            RunId = run.RunId,
            Category = ExecutionEventCategory.RecoveryStarted,
            Title = "Supervisor recovering run",
            Summary = specificHint ?? "Directing model to produce next executable action"
        });

        var sb = new StringBuilder();
        sb.AppendLine("[SYSTEM DIRECTIVE: AUTONOMOUS RUN CONTINUATION]");
        sb.AppendLine("CURRENT TASK IS NOT COMPLETE.");
        sb.AppendLine();
        sb.AppendLine("No executable action occurred in the previous turn.");
        if (!string.IsNullOrWhiteSpace(specificHint))
        {
            sb.AppendLine(specificHint);
        }
        sb.AppendLine();
        sb.AppendLine("Continue execution.");
        sb.AppendLine("Choose the next action required to make measurable progress.");
        sb.AppendLine("Do not explain what you would do. Act immediately.");

        return sb.ToString();
    }

    /// <summary>
    /// Formats the single current autonomous execution state for prompt injection.
    /// </summary>
    public string BuildExecutionStatePrompt(AutonomousRunContext run, string? currentRequirement = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AUTONOMOUS EXECUTION STATE");
        sb.AppendLine();
        sb.AppendLine($"Objective:\n{run.Objective}");
        sb.AppendLine();
        sb.AppendLine($"Status:\n{(run.State == AutonomousRunState.Stalled ? "STALLED — ACTION REQUIRED" : "IN PROGRESS")}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(run.LastTool))
        {
            sb.AppendLine($"Last action:\n{run.LastTool}");
            if (!string.IsNullOrWhiteSpace(run.LastResultSummary))
            {
                sb.AppendLine($"Last result:\n{run.LastResultSummary}");
            }
        }
        if (!string.IsNullOrWhiteSpace(currentRequirement))
        {
            sb.AppendLine($"Current requirement:\n{currentRequirement}");
        }
        sb.AppendLine();
        sb.AppendLine("Next action:");
        sb.AppendLine("Continue execution until the objective is verified.");
        sb.AppendLine("Do not stop merely because a response has been generated.");

        return sb.ToString();
    }

    /// <summary>
    /// Transitions the run to a terminal Completed state.
    /// </summary>
    public void CompleteRun(AutonomousRunContext run, string? summary = null)
    {
        run.State = AutonomousRunState.Completed;
        run.EndedAtUtc = DateTime.UtcNow;
        run.Summary = summary ?? "Objective verified complete.";

        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = run.SessionId,
            TaskId = run.TaskId,
            RunId = run.RunId,
            Category = ExecutionEventCategory.RunCompleted,
            Success = true,
            Title = "Autonomous run completed",
            Summary = run.Summary
        });
    }

    /// <summary>
    /// Transitions the run to a terminal Failed state.
    /// </summary>
    public void FailRun(AutonomousRunContext run, string error)
    {
        run.State = AutonomousRunState.Failed;
        run.EndedAtUtc = DateTime.UtcNow;
        run.Error = error;

        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = run.SessionId,
            TaskId = run.TaskId,
            RunId = run.RunId,
            Category = ExecutionEventCategory.RunCompleted,
            Success = false,
            Title = "Autonomous run failed",
            Summary = error
        });
    }

    /// <summary>
    /// Transitions the run to a terminal Cancelled state.
    /// </summary>
    public void CancelRun(AutonomousRunContext run, string reason = "Cancelled by user.")
    {
        run.State = AutonomousRunState.Cancelled;
        run.EndedAtUtc = DateTime.UtcNow;
        run.Error = reason;

        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = run.SessionId,
            TaskId = run.TaskId,
            RunId = run.RunId,
            Category = ExecutionEventCategory.RunCompleted,
            Success = false,
            Title = "Autonomous run cancelled",
            Summary = reason
        });
    }
}
