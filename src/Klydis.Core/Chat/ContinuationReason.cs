namespace Klydis.Core.Chat;

/// <summary>
/// Why the runtime continued (or stopped) the agent loop after a model generation. Making
/// the reason explicit turns the loop's decisions into diagnosable events — the alternative
/// is the model narrating its own termination ("I hit the message limit"), which is not a
/// continuation signal. The runtime, never the model, produces these.
/// </summary>
public enum ContinuationReason
{
    /// <summary>Iteration continues to execute a parsed tool call.</summary>
    ToolCallPending,

    /// <summary>Model ended its generation but the step/task remains open — resume.</summary>
    StepIncomplete,

    /// <summary>The generation hit the output cap or was cut mid-stream — resume with fresh budget.</summary>
    GenerationTruncated,

    /// <summary>The model emitted its own end-of-turn token mid-sentence repeatedly — decline to churn; wait for the user.</summary>
    ModelEndedEarly,

    /// <summary>Rolling context compression ran; resume against the compacted window.</summary>
    ContextCompacted,

    /// <summary>A task_complete claim was rejected by the deterministic verifier.</summary>
    VerificationFailed,

    /// <summary>Repeated tool turns advanced no durable state — reassessment notice injected.</summary>
    StagnationDetected,

    /// <summary>The iteration cap or turn-duration budget was reached.</summary>
    BudgetExhausted,

    /// <summary>The user cancelled the turn.</summary>
    UserCancelled,

    /// <summary>The harness accepted a completion claim — the task is verified done.</summary>
    CompletionAccepted,

    /// <summary>An error ended the turn.</summary>
    Error
}
