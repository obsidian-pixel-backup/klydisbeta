namespace Klydis.Core.Tracing;

/// <summary>
/// Granular categorization for high-resolution timing scopes across the agent lifecycle.
/// Used to attribute active agent working time versus idle/waiting time, and to analyze
/// performance bottlenecks (model inference, tool execution, web requests, context build, verification).
/// </summary>
public enum AgentTimingCategory
{
    UserWait,
    QueueWait,

    TaskResolution,
    Planning,
    ContextBuild,

    ModelQueueWait,
    ModelInference,
    ModelStreaming,

    Parsing,
    Validation,

    ToolQueueWait,
    ToolExecution,

    SkillExecution,

    WebNetwork,
    WebParsing,
    WebExtraction,

    EvidenceProcessing,
    Verification,

    Compaction,
    Scheduling,

    Other
}
