namespace Klydis.Core.Tasks;

/// <summary>
/// The 6 phases of the deterministic OODA-VR agentic execution cycle.
/// </summary>
public enum AgentLoopPhase
{
    /// <summary>
    /// Phase 1: Workspace inspection, environment observation, reading files/tool outputs,
    /// querying message queues, ingesting external context.
    /// </summary>
    Observe,

    /// <summary>
    /// Phase 2: Situational synthesis, scratchpad reasoning, context evaluation,
    /// hypothesis generation, dependency analysis, gap assessment.
    /// </summary>
    Orient,

    /// <summary>
    /// Phase 3: Action selection, obligation evaluation, strategy formulation,
    /// replanning directives, tool call proposal.
    /// </summary>
    Decide,

    /// <summary>
    /// Phase 4: Action Gate validation, replay protection check, parallel/sequential
    /// tool execution in sandboxed workspace.
    /// </summary>
    Act,

    /// <summary>
    /// Phase 5: Closed-loop verification, compiler diagnostics, test execution,
    /// runtime invariant checks, typed evidence recording.
    /// </summary>
    Verify,

    /// <summary>
    /// Phase 6: Post-action evaluation, plan update, state delta calculation,
    /// lessons learned extraction, scratchpad consolidation, loop continuation/completion check.
    /// </summary>
    Reflect,

    /// <summary>
    /// Terminal state: Goal verified and completed by the supervisor.
    /// </summary>
    Completed,

    /// <summary>
    /// Terminal state: Task failed or aborted.
    /// </summary>
    Failed,

    /// <summary>
    /// Suspended state: Paused for user steering or input.
    /// </summary>
    Paused
}
