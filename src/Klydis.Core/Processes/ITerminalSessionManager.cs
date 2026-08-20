using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Processes;

/// <summary>
/// Task-scoped terminal session and process isolation manager (Phase 12).
/// Manages running commands, daemon processes, and task-isolated sub-processes.
/// </summary>
public interface ITerminalSessionManager
{
    /// <summary>Starts a new managed terminal process associated with an optional task.</summary>
    ProcessStatusReport StartSession(ProcessStartOptions options, string? taskId = null);

    /// <summary>Gets the status report and incremental output deltas for a session.</summary>
    ProcessStatusReport? GetSessionStatus(string processId, bool includeFullOutput = false);

    /// <summary>Sends standard input to a running session.</summary>
    bool SendInput(string processId, string input, bool addNewline = true);

    /// <summary>Kills a running session and its process tree.</summary>
    bool KillSession(string processId, bool entireTree = true);

    /// <summary>Lists all managed sessions, optionally filtered by task.</summary>
    IReadOnlyList<ProcessSummary> ListSessions(string? taskId = null);
}

/// <summary>
/// Concrete implementation of <see cref="ITerminalSessionManager"/> wrapping <see cref="IProcessManager"/>.
/// </summary>
public sealed class TerminalSessionManager : ITerminalSessionManager
{
    private readonly IProcessManager _processManager;
    private readonly ConcurrentDictionary<string, string?> _processTasks = new(StringComparer.Ordinal);

    public TerminalSessionManager(IProcessManager processManager)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
    }

    /// <inheritdoc />
    public ProcessStatusReport StartSession(ProcessStartOptions options, string? taskId = null)
    {
        var report = _processManager.StartProcess(options);
        if (!string.IsNullOrEmpty(report.ProcessId))
        {
            _processTasks[report.ProcessId] = taskId;
        }
        return report;
    }

    /// <inheritdoc />
    public ProcessStatusReport? GetSessionStatus(string processId, bool includeFullOutput = false)
        => _processManager.GetStatus(processId, includeFullOutput);

    /// <inheritdoc />
    public bool SendInput(string processId, string input, bool addNewline = true)
        => _processManager.SendInput(processId, input, addNewline);

    /// <inheritdoc />
    public bool KillSession(string processId, bool entireTree = true)
        => _processManager.KillProcess(processId, entireTree);

    /// <inheritdoc />
    public IReadOnlyList<ProcessSummary> ListSessions(string? taskId = null)
    {
        var all = _processManager.ListProcesses();
        if (string.IsNullOrEmpty(taskId)) return all;

        return all.Where(p => _processTasks.TryGetValue(p.ProcessId, out var tid) &&
                              string.Equals(tid, taskId, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
