using System;

namespace Klydis.Core.Memory;

/// <summary>
/// Categories of prompt segments assembled by <see cref="ContextCompiler"/>.
/// </summary>
public enum PromptSegmentKind
{
    SystemCore,
    ModelProfile,
    CurrentObjective,
    CurrentStep,
    ActionContract,
    ToolIndex,
    ToolDetails,
    PlanSummary,
    RecentActions,
    RecentResults,
    WorldState,
    Queue,
    SkillContext,
    RagContext,
    AttachmentContext,
    ArtifactContext,
    ConversationHistory,
    RecoveryContext
}

/// <summary>
/// How a prompt segment behaves under context budget pressure.
/// </summary>
public enum EvictionPolicy
{
    /// <summary>Never evict or truncate under normal budget constraints.</summary>
    Never,

    /// <summary>Trim oldest lines/entries when budget is exceeded.</summary>
    TrimOldest,

    /// <summary>Drop the entire segment if budget is tight.</summary>
    DropWhenOverBudget,

    /// <summary>Compress or summarize content.</summary>
    Summarize
}

/// <summary>
/// An atomic prompt segment submitted to <see cref="ContextCompiler"/>.
/// </summary>
public sealed record PromptSegment
{
    /// <summary>Segment category.</summary>
    public PromptSegmentKind Kind { get; init; }

    /// <summary>Priority (higher = preserved first under budget pressure, e.g. 100=essential, 10=discretionary).</summary>
    public int Priority { get; init; } = 50;

    /// <summary>Raw text content of the segment.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Estimated token cost of this segment.</summary>
    public int TokenCost { get; init; }

    /// <summary>Whether this segment changes across turns or is cacheable static prefix.</summary>
    public bool Mutable { get; init; } = true;

    /// <summary>Whether the prompt compiler must fail or escalate if this segment is omitted.</summary>
    public bool Required { get; init; }

    /// <summary>Eviction behavior under budget limits.</summary>
    public EvictionPolicy EvictionPolicy { get; init; } = EvictionPolicy.DropWhenOverBudget;

    /// <summary>Reason why this segment was included (for prompt telemetry / context inspector).</summary>
    public string? Reason { get; init; }
}
