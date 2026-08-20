using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.Tasks;

/// <summary>
/// Request to execute a tool action within a specific task, run, and step context.
/// </summary>
public sealed record ActionRequest(
    string ActionId,
    string TaskId,
    string? RunId,
    string? StepId,
    string ToolName,
    IDictionary<string, object>? Arguments,
    string? SessionId = null);

/// <summary>
/// Result of executing a tool action through the action layer.
/// </summary>
public sealed record ActionResult(
    string ActionId,
    bool Success,
    string? OutputPreview,
    string? Error,
    int? ExitCode = null,
    object? RawResult = null,
    bool IsReplay = false);

/// <summary>
/// Interface for executing tool actions against the environment through policy and validation gates.
/// </summary>
public interface IActionExecutor
{
    /// <summary>
    /// Executes the specified action request and returns its durable execution outcome.
    /// </summary>
    Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default);
}
