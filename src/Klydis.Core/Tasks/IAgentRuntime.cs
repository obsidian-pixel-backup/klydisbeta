using System;
using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.Tasks;

/// <summary>
/// Result of an autonomous agent runtime execution run.
/// </summary>
public sealed record RunResult(
    string RunId,
    string TaskId,
    RunStatus Status,
    int TurnCount,
    string? Summary = null,
    string? Error = null)
{
    /// <summary>True when the run reached a clean completed state.</summary>
    public bool IsSuccess => Status == RunStatus.Completed;
}

/// <summary>
/// The primary interface for the autonomous agent runtime.
/// Drives the execution loop, lifecycle, and supervision independently of any UI view model.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Executes the specified task through its autonomous run lifecycle until a terminal state
    /// (Completed, Failed, Paused, Cancelled) is reached.
    /// </summary>
    Task<RunResult> RunAsync(string taskId, CancellationToken cancellationToken = default);
}
