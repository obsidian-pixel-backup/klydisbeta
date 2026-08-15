using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Diagnostics;

/// <summary>
/// Runs fire-and-forget async work with guaranteed exception handling. Unobserved task
/// exceptions (from bare <c>_ = Task.Run(...)</c> / <c>_ = SomeAsync()</c> sites) would
/// otherwise be swallowed silently or crash the process depending on the runtime; this helper
/// catches them and logs to the provided logger (or the rotating chat_debug.log as fallback).
/// </summary>
public static class FireAndForget
{
    /// <summary>
    /// Runs an async delegate on the threadpool. Exceptions are caught and logged.
    /// </summary>
    public static void Run(Func<Task> action, ILogger? logger = null, string? operation = null)
    {
        if (action == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFailure(logger, operation, ex);
            }
        });
    }

    /// <summary>
    /// Observes an already-started task so its eventual failure is logged instead of becoming
    /// an unobserved exception.
    /// </summary>
    public static void Observe(Task task, ILogger? logger = null, string? operation = null)
    {
        if (task == null) return;

        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                LogFailure(logger, operation, t.Exception.GetBaseException());
            }
        }, TaskScheduler.Default);
    }

    private static void LogFailure(ILogger? logger, string? operation, Exception ex)
    {
        var op = operation ?? "background task";
        if (logger != null)
        {
            logger.LogError(ex, "Fire-and-forget task '{Operation}' failed.", op);
        }
        else
        {
            KlydisLog.AppendChatDebug($"[{DateTime.Now:HH:mm:ss.fff}] FIRE-AND-FORGET '{op}' FAILED: {ex}{Environment.NewLine}");
        }
    }
}
