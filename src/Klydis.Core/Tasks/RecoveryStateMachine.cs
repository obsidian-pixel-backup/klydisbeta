using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// Finite states in the deterministic failure recovery state machine.
/// Prevents the model from entering endless self-reasoning repair loops.
/// </summary>
public enum RecoveryState
{
    /// <summary>Normal execution cycle.</summary>
    Normal,

    /// <summary>Tool failed or was rejected by ActionGate.</summary>
    ToolFailure,

    /// <summary>Attempting deterministic syntactic or schema correction (no model tokens).</summary>
    DeterministicRepair,

    /// <summary>Targeted model repair with a minimal prompt and tight token budget.</summary>
    ModelRepair,

    /// <summary>Previous strategy blocked; requesting an alternative action.</summary>
    AlternativeStrategy,

    /// <summary>Automated recovery exhausted; pausing for user clarification or intervention.</summary>
    UserInputRequired,

    /// <summary>Terminal failure for the step or run.</summary>
    Blocked
}

/// <summary>
/// Recovery events that drive transitions in <see cref="RecoveryStateMachine"/>.
/// </summary>
public enum RecoveryEvent
{
    ExecutionSucceeded,
    DeterministicRepairSucceeded,
    DeterministicRepairFailed,
    ModelRepairSucceeded,
    ModelRepairFailed,
    StrategyBlocked,
    ExhaustedRetries,
    UserIntervened
}

/// <summary>
/// Finite State Machine governing tool failures, schema repairs, and recovery escalation.
/// </summary>
public sealed class RecoveryStateMachine
{
    public RecoveryState CurrentState { get; private set; } = RecoveryState.Normal;

    public int ToolExecutionAttempts { get; private set; } = 0;
    public int SchemaRepairAttempts { get; private set; } = 0;
    public int ModelRepairAttempts { get; private set; } = 0;
    public int SameActionRetries { get; private set; } = 0;

    public const int MaxToolExecutionAttempts = 2;
    public const int MaxSchemaRepairAttempts = 1;
    public const int MaxModelRepairAttempts = 2;
    public const int MaxSameActionRetries = 1;

    /// <summary>
    /// Evaluates a failure event and transitions to the next recovery state.
    /// </summary>
    public RecoveryState Transition(RecoveryEvent @event)
    {
        CurrentState = (CurrentState, @event) switch
        {
            (RecoveryState.Normal, RecoveryEvent.ExecutionSucceeded) => RecoveryState.Normal,
            (RecoveryState.Normal, RecoveryEvent.StrategyBlocked) => RecoveryState.AlternativeStrategy,
            (RecoveryState.Normal, _) => HandleFailureInitial(),

            (RecoveryState.ToolFailure, RecoveryEvent.DeterministicRepairSucceeded) => ResetToNormal(),
            (RecoveryState.ToolFailure, RecoveryEvent.DeterministicRepairFailed) => EscalateAfterDeterministicRepair(),
            (RecoveryState.ToolFailure, RecoveryEvent.StrategyBlocked) => RecoveryState.AlternativeStrategy,

            (RecoveryState.DeterministicRepair, RecoveryEvent.DeterministicRepairSucceeded) => ResetToNormal(),
            (RecoveryState.DeterministicRepair, RecoveryEvent.DeterministicRepairFailed) => EscalateAfterDeterministicRepair(),

            (RecoveryState.ModelRepair, RecoveryEvent.ModelRepairSucceeded) => ResetToNormal(),
            (RecoveryState.ModelRepair, RecoveryEvent.ModelRepairFailed) => EscalateAfterModelRepair(),
            (RecoveryState.ModelRepair, RecoveryEvent.StrategyBlocked) => RecoveryState.AlternativeStrategy,

            (RecoveryState.AlternativeStrategy, RecoveryEvent.ExecutionSucceeded) => ResetToNormal(),
            (RecoveryState.AlternativeStrategy, RecoveryEvent.ExhaustedRetries) => RecoveryState.UserInputRequired,

            (RecoveryState.UserInputRequired, RecoveryEvent.UserIntervened) => ResetToNormal(),
            (RecoveryState.Blocked, RecoveryEvent.UserIntervened) => ResetToNormal(),

            _ => CurrentState
        };

        return CurrentState;
    }

    private RecoveryState HandleFailureInitial()
    {
        ToolExecutionAttempts++;
        if (ToolExecutionAttempts > MaxToolExecutionAttempts)
        {
            return RecoveryState.AlternativeStrategy;
        }
        return RecoveryState.DeterministicRepair;
    }

    private RecoveryState EscalateAfterDeterministicRepair()
    {
        SchemaRepairAttempts++;
        return EscalateToModelRepair();
    }

    private RecoveryState EscalateToModelRepair()
    {
        ModelRepairAttempts++;
        if (ModelRepairAttempts > MaxModelRepairAttempts)
        {
            return RecoveryState.AlternativeStrategy;
        }
        return RecoveryState.ModelRepair;
    }

    private RecoveryState EscalateAfterModelRepair()
    {
        ModelRepairAttempts++;
        if (ModelRepairAttempts <= MaxModelRepairAttempts)
        {
            return RecoveryState.ModelRepair;
        }
        return RecoveryState.AlternativeStrategy;
    }

    private RecoveryState ResetToNormal()
    {
        ToolExecutionAttempts = 0;
        SchemaRepairAttempts = 0;
        ModelRepairAttempts = 0;
        SameActionRetries = 0;
        return RecoveryState.Normal;
    }

    /// <summary>
    /// Resets all counters on clean turn or task change.
    /// </summary>
    public void Reset()
    {
        ResetToNormal();
    }
}
