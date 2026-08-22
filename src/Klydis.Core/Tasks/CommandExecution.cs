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
    PathNotFound,
    Timeout,
    ProcessCrash,
    ShellSyntaxError,
    ParserError,
    ToolUnavailable,
    NetworkError,
    AuthenticationError,
    ResourceLimit,
    OutputLimit,
    WorkingDirectoryInvalid,
    EnvironmentError,
    NonZeroExit,
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
            lower.Contains("cannot find the path specified") && lower.Contains("command") ||
            lower.Contains("no such file or directory") && (lower.Contains("bin") || lower.Contains("exe")))
        {
            return CommandErrorClassification.CommandNotFound;
        }

        if (lower.Contains("access is denied") ||
            lower.Contains("permission denied") ||
            lower.Contains("unauthorizedaccessexception") ||
            lower.Contains("requires elevation") ||
            lower.Contains("run as administrator"))
        {
            return CommandErrorClassification.PermissionDenied;
        }

        if (lower.Contains("cannot find path") ||
            lower.Contains("path does not exist") ||
            lower.Contains("the system cannot find the path specified") ||
            lower.Contains("directory not found"))
        {
            return CommandErrorClassification.PathNotFound;
        }

        if (lower.Contains("working directory does not exist") ||
            lower.Contains("invalid working directory"))
        {
            return CommandErrorClassification.WorkingDirectoryInvalid;
        }

        if (lower.Contains("syntax error") ||
            lower.Contains("unexpected token") ||
            lower.Contains("parsererror") ||
            lower.Contains("invalid characters in path") ||
            lower.Contains("the token '") ||
            lower.Contains("at line:") && lower.Contains("char:"))
        {
            return CommandErrorClassification.ShellSyntaxError;
        }

        if (lower.Contains("missing an argument") ||
            lower.Contains("parameter format not correct") ||
            lower.Contains("invalid parameter") ||
            lower.Contains("a positional parameter cannot be found") ||
            lower.Contains("cannot bind argument"))
        {
            return CommandErrorClassification.InvalidArgument;
        }

        if (lower.Contains("output exceeded") || lower.Contains("context budget") || lower.Contains("tool output exceeded"))
        {
            return CommandErrorClassification.OutputLimit;
        }

        if (lower.Contains("connection refused") ||
            lower.Contains("network is unreachable") ||
            lower.Contains("name or service not known") ||
            lower.Contains("could not resolve host"))
        {
            return CommandErrorClassification.NetworkError;
        }

        if (lower.Contains("out of memory") || lower.Contains("insufficient system resources"))
        {
            return CommandErrorClassification.ResourceLimit;
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

    public static string GetGuidance(CommandErrorClassification classification) => classification switch
    {
        CommandErrorClassification.CommandNotFound =>
            "COMMAND_NOT_FOUND: The executable/cmdlet was not found on this machine. Use a standard PowerShell cmdlet, a native tool, or verify the executable exists in PATH.",
        CommandErrorClassification.InvalidArgument =>
            "INVALID_ARGUMENT: A positional or named parameter is invalid or missing. Check parameter names and syntax.",
        CommandErrorClassification.PermissionDenied =>
            "PERMISSION_DENIED: Access denied or elevation required. Select an alternative non-privileged command or approach.",
        CommandErrorClassification.PathNotFound =>
            "PATH_NOT_FOUND: Target path does not exist. Inspect parent directory with 'list_directory' or verify file path before retrying.",
        CommandErrorClassification.Timeout =>
            "TIMEOUT: Command exceeded timeout budget. Split operation into smaller batches or optimize query parameters.",
        CommandErrorClassification.ShellSyntaxError =>
            "SHELL_SYNTAX_ERROR: Command syntax is invalid for the shell. Avoid Linux-only syntax on Windows (e.g. 4>/dev/null, head/grep). Write pure PowerShell or CMD syntax.",
        CommandErrorClassification.OutputLimit =>
            "OUTPUT_TOO_LARGE: Tool output exceeded context limit. Filter or pipe results (e.g. Select-Object -First 25 or pagination).",
        CommandErrorClassification.WorkingDirectoryInvalid =>
            "INVALID_WORKING_DIRECTORY: Specified working directory does not exist. Use valid project directory or omit working_directory.",
        CommandErrorClassification.NetworkError =>
            "NETWORK_ERROR: Network resource unreachable. Verify host address and connection.",
        CommandErrorClassification.ResourceLimit =>
            "RESOURCE_LIMIT: Operation hit memory or system limit.",
        CommandErrorClassification.NonZeroExit =>
            "NON_ZERO_EXIT: Command returned a non-zero exit code. Review stderr output to determine root cause and adjust command parameters.",
        _ => "COMMAND_FAILED: Review stderr and stdout output to adjust command."
    };
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
