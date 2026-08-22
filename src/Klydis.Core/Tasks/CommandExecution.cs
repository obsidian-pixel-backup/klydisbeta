using System;
using System.Text.Json;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// States for the command execution lifecycle state machine.
/// Every command moves through these states deterministically.
/// </summary>
public enum CommandState
{
    Proposed,
    Validating,
    Valid,
    Queued,
    Executing,
    Completed,
    Rejected,
    RepairRequired,
    Failed,
    Retryable
}

/// <summary>
/// Typed classification for command execution errors.
/// Enables deterministic recovery and model feedback.
/// </summary>
public enum CommandErrorClassification
{
    None,
    CommandNotFound,
    InvalidArgument,
    PermissionDenied,
    Timeout,
    ProcessCrash,
    NonZeroExit,
    OutputLimit,
    WorkingDirectoryInvalid,
    EnvironmentError,
    Cancelled,
    Unknown
}

/// <summary>
/// First-class command execution entity representing a concrete tool execution attempt.
/// </summary>
public sealed class CommandExecution
{
    public required string Id { get; init; }
    public required string GoalId { get; init; }
    public required string TurnId { get; init; }
    public required string ToolName { get; init; }
    public JsonDocument? Arguments { get; init; }
    public string? RawArgumentsJson { get; init; }
    public CommandState State { get; set; } = CommandState.Proposed;
    public int Attempt { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ToolResult? Result { get; set; }
    public int? ExitCode { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public long DurationMs { get; set; }
    public bool TimedOut { get; set; }
    public bool Cancelled { get; set; }
    public CommandErrorClassification ErrorClassification { get; set; } = CommandErrorClassification.None;
    public string? RecoveryGuidance { get; set; }

    public static CommandErrorClassification ClassifyError(string? error, string? output, int? exitCode, bool timedOut, bool cancelled)
    {
        if (cancelled) return CommandErrorClassification.Cancelled;
        if (timedOut) return CommandErrorClassification.Timeout;

        string combined = (error ?? "") + " " + (output ?? "");
        string lower = combined.ToLowerInvariant();

        if (lower.Contains("is not recognized as an internal or external command") ||
            lower.Contains("the term '") && lower.Contains("' is not recognized") ||
            lower.Contains("command not found") ||
            lower.Contains("cannot find the path specified") && lower.Contains("command"))
        {
            return CommandErrorClassification.CommandNotFound;
        }

        if (lower.Contains("access is denied") ||
            lower.Contains("permission denied") ||
            lower.Contains("unauthorizedaccessexception"))
        {
            return CommandErrorClassification.PermissionDenied;
        }

        if (lower.Contains("working directory does not exist") ||
            lower.Contains("invalid working directory"))
        {
            return CommandErrorClassification.WorkingDirectoryInvalid;
        }

        if (lower.Contains("missing an argument") ||
            lower.Contains("parameter format not correct") ||
            lower.Contains("invalid parameter") ||
            lower.Contains("a positional parameter cannot be found"))
        {
            return CommandErrorClassification.InvalidArgument;
        }

        if (lower.Contains("output exceeded") || lower.Contains("context budget"))
        {
            return CommandErrorClassification.OutputLimit;
        }

        if (exitCode.HasValue && exitCode.Value != 0)
        {
            return CommandErrorClassification.NonZeroExit;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return CommandErrorClassification.Unknown;
        }

        return CommandErrorClassification.None;
    }
}

/// <summary>
/// Runtime entity representing one model generation cycle.
/// </summary>
public sealed record ModelGenerationRecord(
    string GenerationId,
    string GoalId,
    string TurnId,
    string? ModelId,
    int OutputTokens,
    string FinishReason,
    DateTimeOffset Timestamp);

/// <summary>
/// Runtime entity representing one structured tool call request.
/// </summary>
public sealed record ToolCallRecord(
    string CallId,
    string GoalId,
    string TurnId,
    string ToolName,
    string ArgumentsJson,
    DateTimeOffset Timestamp);

/// <summary>
/// Runtime entity representing a tool result received from execution.
/// </summary>
public sealed record ToolResultRecord(
    string ResultId,
    string CallId,
    string GoalId,
    string TurnId,
    bool Success,
    int? ExitCode,
    string Stdout,
    string? Stderr,
    CommandErrorClassification ErrorClassification,
    DateTimeOffset Timestamp);

/// <summary>
/// Runtime entity representing one complete model decision turn.
/// </summary>
public sealed record AgentTurnRecord(
    string TurnId,
    string GoalId,
    int TurnNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Objective,
    IReadOnlyList<ToolCallRecord> ProposedActions,
    IReadOnlyList<ToolResultRecord> ExecutedResults,
    string ContinuationVerdict);
