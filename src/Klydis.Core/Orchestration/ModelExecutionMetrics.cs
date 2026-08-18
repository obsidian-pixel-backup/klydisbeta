using System;
using System.Collections.Generic;

namespace Klydis.Core.Orchestration;

/// <summary>
/// Aggregated execution metrics for one (ModelId, ProtocolKey) pair, computed from the
/// durable action/step/run ledger (agent-intelligence stage §3). These are the raw observed
/// rates that feed <see cref="ModelCapabilityEstimator"/>: the estimator smooths them toward
/// a prior so a single sample cannot swing a capability score, and so a model with no
/// history still has a conservative, honest profile.
/// </summary>
public sealed record ModelExecutionMetrics(
    string ModelId,
    string ProtocolKey,
    int TotalActions,
    int SucceededActions,
    int FailedActions,
    int TimedOutActions,
    int CancelledActions,
    int UnknownActions,
    int StepsTouched,
    int FirstActionsSucceeded,
    int FirstActionsAttempted,
    int StepsWithMultipleActions,
    int StepsWithAnyAction,
    int VerificationActionsSucceeded,
    int VerificationActionsAttempted,
    int FileMutationActionsSucceeded,
    int FileMutationActionsAttempted,
    int RunsAttributed,
    int RunsCompleted)
{
    /// <summary>Fraction of terminal actions that succeeded (Unknown is NOT success —
    /// conservative: the outcome is unproven). 1.0 when there are no terminal actions.</summary>
    public double ToolSuccessRate
    {
        get
        {
            int terminal = SucceededActions + FailedActions + TimedOutActions;
            return terminal == 0 ? 1.0 : (double)SucceededActions / terminal;
        }
    }

    /// <summary>Fraction of steps whose FIRST action succeeded — the "got it right the first
    /// time" signal that distinguishes a reliable model from a repair-heavy one. 1.0 when
    /// no first-action was observed.</summary>
    public double FirstActionSuccessRate
        => FirstActionsAttempted == 0 ? 1.0 : (double)FirstActionsSucceeded / FirstActionsAttempted;

    /// <summary>Mean number of actions per touched step (1.0 = one-shot; higher = more
    /// churn/repair per step). 0 when no step was touched.</summary>
    public double MeanActionsPerStep
        => StepsTouched == 0 ? 0.0 : (double)TotalActions / StepsTouched;

    /// <summary>Fraction of touched steps that needed MORE than one action — a repair-rate
    /// proxy (a step that completes on its first action needed no repair).</summary>
    public double RepairRate
        => StepsWithAnyAction == 0 ? 0.0 : (double)StepsWithMultipleActions / StepsWithAnyAction;

    /// <summary>Fraction of verification-kind actions that succeeded — how reliably the model
    /// performs (or drives) verification. 1.0 when no verification action was observed.</summary>
    public double VerificationSuccessRate
        => VerificationActionsAttempted == 0 ? 1.0 : (double)VerificationActionsSucceeded / VerificationActionsAttempted;

    /// <summary>Fraction of file-mutation actions (write_file/edit_file) that succeeded — the
    /// coding signal. 1.0 when no mutation was observed.</summary>
    public double FileMutationSuccessRate
        => FileMutationActionsAttempted == 0 ? 1.0 : (double)FileMutationActionsSucceeded / FileMutationActionsAttempted;

    /// <summary>Fraction of runs attributed to this model that completed cleanly — the
    /// end-to-end reliability signal. 1.0 when no run is attributed.</summary>
    public double CompletionRate
        => RunsAttributed == 0 ? 1.0 : (double)RunsCompleted / RunsAttributed;

    /// <summary>Total terminal observations used by the estimator's confidence weight.</summary>
    public int SampleCount => SucceededActions + FailedActions + TimedOutActions + UnknownActions;

    /// <summary>Short diagnostic line, e.g. "qwen3.6-14b | qwen-native | 42 actions, 88% tool success".</summary>
    public override string ToString()
        => $"{ModelId} | {ProtocolKey} | {TotalActions} actions, {ToolSuccessRate:P0} tool success, " +
           $"{FirstActionSuccessRate:P0} first-action, {RepairRate:P0} repair, " +
           $"{VerificationSuccessRate:P0} verification, {CompletionRate:P0} completion";
}
