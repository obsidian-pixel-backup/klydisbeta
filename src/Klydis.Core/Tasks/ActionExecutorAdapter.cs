using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Adapter that connects the high-level <see cref="IActionExecutor"/> interface to the underlying <see cref="ToolExecutor"/>
/// and provides replay cache resolution.
/// </summary>
public sealed class ActionExecutorAdapter : IActionExecutor
{
    private readonly ToolExecutor _toolExecutor;
    private readonly Memory.MessageStore? _store;

    public ActionExecutorAdapter(ToolExecutor toolExecutor, Memory.MessageStore? store = null)
    {
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _store = store;
    }

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request == null)
        {
            return new ActionResult(
                ActionId: string.Empty,
                Success: false,
                OutputPreview: null,
                Error: "ActionRequest cannot be null.");
        }

        var callRequest = new ToolCallRequest(request.ToolName, request.Arguments ?? new Dictionary<string, object>());

        // Check durable action ledger for replay if store is present
        if (_store != null && !string.IsNullOrEmpty(request.TaskId))
        {
            var replayKey = ActionGate.ComputeReplayKey(callRequest);
            var pastActions = await _store.GetTaskActionsAsync(request.TaskId, request.RunId);
            var existing = pastActions.FirstOrDefault(a => a.ReplayKey == replayKey && a.Status == ActionExecutionStatus.Succeeded);
            if (existing != null)
            {
                return new ActionResult(
                    ActionId: existing.ActionId,
                    Success: true,
                    OutputPreview: existing.ResultPreview,
                    Error: null,
                    RawResult: null,
                    IsReplay: true);
            }
        }
        try
        {
            var result = await _toolExecutor.ExecuteToolAsync(
                callRequest,
                request.SessionId ?? string.Empty,
                cancellationToken);

            return new ActionResult(
                ActionId: request.ActionId,
                Success: result.Success,
                OutputPreview: result.Output,
                Error: result.Error,
                ExitCode: result.ExitCode,
                RawResult: result);
        }
        catch (Exception ex)
        {
            return new ActionResult(
                ActionId: request.ActionId,
                Success: false,
                OutputPreview: null,
                Error: ex.Message);
        }
    }
}
