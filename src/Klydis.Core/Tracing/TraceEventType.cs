using System;

namespace Klydis.Core.Tracing;

/// <summary>
/// Strongly-typed categories of agent execution and diagnostic trace events.
/// Every state change, model interaction, parser decision, tool dispatch,
/// and continuation evaluation in the agent lifecycle emits an event of this type.
/// </summary>
public enum TraceEventType
{
    // Session & User
    SessionStarted,
    SessionEnded,
    UserMessage,

    // Task & Run Lifecycle
    TaskCreated,
    TaskSelected,
    TaskStateChanged,
    RunStarted,
    RunCompleted,
    RunFailed,
    RunTerminated,

    // Turn & Cycle Lifecycle
    TurnStarted,
    TurnCompleted,
    CycleStarted,
    CycleCompleted,

    // Model Inference & Generation
    InferenceStarted,
    InferenceCompleted,
    GenerationStarted,
    FirstTokenReceived,
    LastTokenReceived,
    GenerationCompleted,
    RawModelOutput,
    ModelOutput,
    ModelParseResult,
    OutputParsed,
    OutputParseFailed,
    OutputRejected,

    // Tool Lifecycle
    ToolCallProposed,
    ToolCallParsed,
    ToolCallValidated,
    ToolCallDispatched,
    ToolCallRejected,
    ToolExecutionStarted,
    ToolProcessStarted,
    ToolProcessExited,
    ToolExecutionCompleted,
    ToolExecutionFailed,
    ToolResultDelivered,
    ToolResultInjected,

    // Skill Lifecycle
    SkillDiscoveryStarted,
    SkillDiscoveryResult,
    SkillDiscovered,
    SkillSelected,
    SkillInvocationStarted,
    SkillInvocationCompleted,
    SkillInvocationFailed,
    SkillInvoked,
    SkillCompleted,
    SkillFailed,

    // Web & Browser Operations
    WebSearchStarted,
    WebSearchCompleted,
    SearchResultSelected,
    PageOpened,
    PageFetched,
    PageParsed,
    ContentExtractionStarted,
    ContentExtractionCompleted,
    ScrapeStarted,
    ScrapeCompleted,
    BrowserNavigation,

    // Plan & Steps
    PlanCreated,
    PlanChanged,
    PlanModified,
    StepSelected,
    StepVerified,

    // Work Items & Evidence
    WorkItemCreated,
    WorkItemStarted,
    WorkItemAction,
    WorkItemEvidence,
    WorkItemCompleted,
    WorkItemFailed,
    WorkItemSelected,
    EvidenceCreated,
    VerificationStarted,
    VerificationCompleted,

    // Queue Operations
    QueueChanged,
    QueueMessageAdded,
    QueueMessageSelected,
    QueueMessageIncorporated,
    QueueMessageDeferred,
    QueueMessageCompleted,
    QueueSnapshot,

    // Context & Memory Compaction
    ContextBuildStarted,
    ContextBuilt,
    CompactionStarted,
    CompactionCompleted,

    // Budget & Resource Accounting
    BudgetChanged,
    BudgetSnapshot,

    // Recovery, Repair, and Continuation Decisions
    RetryStarted,
    RepairStarted,
    RepairCompleted,
    RepairFailed,
    ReflectionStarted,
    ContinuationDecision,
    ContinuationTriggered,

    // Artifacts & Files
    ArtifactCreated,
    ArtifactUpdated,

    // Errors & Crashes
    Error,
    Crash
}
