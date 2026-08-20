using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// State machine enforcing valid lifecycle transitions for tool actions (Phase 5).
/// Prevents illegal state jumps and defines crash recovery transitions for Unknown state.
/// </summary>
public static class ActionLifecycleStateMachine
{
    /// <summary>
    /// Evaluates whether transitioning from <paramref name="from"/> to <paramref name="to"/> is valid.
    /// </summary>
    public static bool CanTransition(ActionExecutionStatus from, ActionExecutionStatus to)
    {
        return (from, to) switch
        {
            // Initial Pending transitions
            (ActionExecutionStatus.Pending, ActionExecutionStatus.Prepared) => true,
            (ActionExecutionStatus.Pending, ActionExecutionStatus.InProgress) => true,
            (ActionExecutionStatus.Pending, ActionExecutionStatus.Cancelled) => true,

            // Prepared transitions
            (ActionExecutionStatus.Prepared, ActionExecutionStatus.InProgress) => true,
            (ActionExecutionStatus.Prepared, ActionExecutionStatus.Cancelled) => true,

            // InProgress execution outcomes
            (ActionExecutionStatus.InProgress, ActionExecutionStatus.Succeeded) => true,
            (ActionExecutionStatus.InProgress, ActionExecutionStatus.Failed) => true,
            (ActionExecutionStatus.InProgress, ActionExecutionStatus.TimedOut) => true,
            (ActionExecutionStatus.InProgress, ActionExecutionStatus.Cancelled) => true,
            (ActionExecutionStatus.InProgress, ActionExecutionStatus.Unknown) => true,

            // Crash recovery resolution for Unknown actions
            (ActionExecutionStatus.Unknown, ActionExecutionStatus.Succeeded) => true,
            (ActionExecutionStatus.Unknown, ActionExecutionStatus.Failed) => true,
            (ActionExecutionStatus.Unknown, ActionExecutionStatus.Cancelled) => true,

            // All other transitions are forbidden
            _ => false
        };
    }
}
