using System;

namespace Klydis.Core.Inference;

/// <summary>
/// The specific inference operation being executed. Each operation has an independent
/// context budget and generation ceiling.
/// </summary>
public enum InferenceOperation
{
    Conversation,
    ToolSelection,
    SimpleAction,
    Planning,
    VerificationInterpretation,
    Repair,
    RequirementsExtraction,
    FinalResponse
}

/// <summary>
/// A calculated inference budget for an individual generation turn.
/// </summary>
public sealed record InferenceBudget(
    int ContextLimit,
    int InputTokens,
    int MaxOutputTokens,
    int ReasoningBudget,
    int SafetyMargin,
    InferenceOperation Operation)
{
    /// <summary>
    /// Available token headroom before hitting the context limit minus safety reserve.
    /// </summary>
    public int AvailableHeadroom => Math.Max(0, ContextLimit - InputTokens - SafetyMargin);
}

/// <summary>
/// Describes a budget exhaustion condition across the budget hierarchy.
/// </summary>
public sealed record BudgetExhaustion(
    string Scope,
    string Reason,
    long ConsumedTokens,
    long BudgetLimit,
    bool IsRecoverable);

/// <summary>
/// Central budget manager contract governing context allocation, output clamping,
/// safety reserves, and hierarchical task budgets.
/// </summary>
public interface IBudgetManager
{
    /// <summary>
    /// Calculates the legal generation budget for a specific operation, clamping
    /// MaxOutputTokens to respect the context limit and safety margin.
    /// </summary>
    InferenceBudget CalculateBudget(
        InferenceOperation operation,
        int currentInputTokens,
        int modelReportedContextSize);

    /// <summary>
    /// Returns the maximum allowed input/prompt token budget for the given operation.
    /// </summary>
    int GetMaxInputBudget(InferenceOperation operation, int modelReportedContextSize);

    /// <summary>
    /// Records token consumption against the task, run, and step budgets.
    /// </summary>
    void RecordUsage(
        string? taskId,
        string? runId,
        string? stepId,
        int promptTokens,
        int completionTokens,
        int thinkingTokens);

    /// <summary>
    /// Evaluates whether any level of the budget hierarchy has been exhausted.
    /// </summary>
    BudgetExhaustion? CheckExhaustion(string? taskId, string? runId, string? stepId);

    /// <summary>
    /// Resets budgets for a given task or run.
    /// </summary>
    void Reset(string? taskId, string? runId = null);
}
