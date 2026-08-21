using System;
using System.Collections.Concurrent;

namespace Klydis.Core.Inference;

/// <summary>
/// Default implementation of <see cref="IBudgetManager"/> providing three-tier budget management:
/// Context budget, generation budget, and hierarchical task-cycle budget.
/// </summary>
public sealed class BudgetManager : IBudgetManager
{
    /// <summary>Default context ceiling for local models (32K tokens) to maintain low latency and prevent attention degradation.</summary>
    public const int DefaultLocalContextCeiling = 32_768;

    /// <summary>Hard safety reserve tokens guaranteed for output generation.</summary>
    public const int DefaultSafetyMargin = 4_096;

    /// <summary>Maximum tokens allocated to an entire task across all runs.</summary>
    public const long DefaultTaskBudgetLimit = 1_000_000;

    /// <summary>Maximum tokens allocated to a single continuous run attempt.</summary>
    public const long DefaultRunBudgetLimit = 250_000;

    /// <summary>Maximum tokens allocated to a single step.</summary>
    public const long DefaultStepBudgetLimit = 25_000;

    /// <summary>Maximum tokens allocated to a single turn.</summary>
    public const long DefaultTurnBudgetLimit = 8_000;

    private readonly ConcurrentDictionary<string, long> _taskUsage = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _runUsage = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _stepUsage = new(StringComparer.Ordinal);

    /// <summary>Custom context ceiling override (if set).</summary>
    public int? ContextCeilingOverride { get; set; }

    /// <summary>Custom safety margin override (if set).</summary>
    public int? SafetyMarginOverride { get; set; }

    /// <inheritdoc />
    public InferenceBudget CalculateBudget(
        InferenceOperation operation,
        int currentInputTokens,
        int modelReportedContextSize)
    {
        int effectiveContextCeiling = ContextCeilingOverride ?? (modelReportedContextSize > 0
            ? Math.Min(modelReportedContextSize, DefaultLocalContextCeiling)
            : DefaultLocalContextCeiling);

        int safetyMargin = SafetyMarginOverride ?? (effectiveContextCeiling <= 8192 ? 1024 : DefaultSafetyMargin);
        int maxSafeInput = Math.Max(512, effectiveContextCeiling - safetyMargin);

        int nominalOutput = operation switch
        {
            InferenceOperation.ToolSelection => 1024,
            InferenceOperation.SimpleAction => 1536,
            InferenceOperation.Planning => 3072,
            InferenceOperation.VerificationInterpretation => 1536,
            InferenceOperation.Repair => 1024,
            InferenceOperation.RequirementsExtraction => 2048,
            InferenceOperation.FinalResponse => 2048,
            InferenceOperation.Conversation => 2048,
            _ => 1536
        };

        int nominalReasoning = operation switch
        {
            InferenceOperation.Planning => 2048,
            InferenceOperation.Repair => 512,
            _ => 1024
        };

        int availableOutputHeadroom = Math.Max(128, effectiveContextCeiling - currentInputTokens - (safetyMargin / 2));
        int clampedOutput = Math.Min(nominalOutput, availableOutputHeadroom);

        return new InferenceBudget(
            ContextLimit: effectiveContextCeiling,
            InputTokens: currentInputTokens,
            MaxOutputTokens: clampedOutput,
            ReasoningBudget: Math.Min(nominalReasoning, clampedOutput),
            SafetyMargin: safetyMargin,
            Operation: operation);
    }

    /// <inheritdoc />
    public int GetMaxInputBudget(InferenceOperation operation, int modelReportedContextSize)
    {
        int contextCeiling = ContextCeilingOverride ?? (modelReportedContextSize > 0
            ? Math.Min(modelReportedContextSize, DefaultLocalContextCeiling)
            : DefaultLocalContextCeiling);

        int safetyMargin = SafetyMarginOverride ?? (contextCeiling <= 8192 ? 1024 : DefaultSafetyMargin);

        return operation switch
        {
            InferenceOperation.ToolSelection => Math.Min(12_288, contextCeiling - safetyMargin),
            InferenceOperation.SimpleAction => Math.Min(16_384, contextCeiling - safetyMargin),
            InferenceOperation.Planning => Math.Min(24_576, contextCeiling - safetyMargin),
            InferenceOperation.VerificationInterpretation => Math.Min(16_384, contextCeiling - safetyMargin),
            InferenceOperation.Repair => Math.Min(16_384, contextCeiling - safetyMargin),
            InferenceOperation.RequirementsExtraction => Math.Min(16_384, contextCeiling - safetyMargin),
            InferenceOperation.FinalResponse => Math.Min(12_288, contextCeiling - safetyMargin),
            InferenceOperation.Conversation => Math.Min(16_384, contextCeiling - safetyMargin),
            _ => contextCeiling - safetyMargin
        };
    }

    /// <inheritdoc />
    public void RecordUsage(
        string? taskId,
        string? runId,
        string? stepId,
        int promptTokens,
        int completionTokens,
        int thinkingTokens)
    {
        long totalTurnTokens = promptTokens + completionTokens + thinkingTokens;

        if (!string.IsNullOrEmpty(taskId))
        {
            _taskUsage.AddOrUpdate(taskId, totalTurnTokens, (_, existing) => existing + totalTurnTokens);
        }
        if (!string.IsNullOrEmpty(runId))
        {
            _runUsage.AddOrUpdate(runId, totalTurnTokens, (_, existing) => existing + totalTurnTokens);
        }
        if (!string.IsNullOrEmpty(stepId))
        {
            _stepUsage.AddOrUpdate(stepId, totalTurnTokens, (_, existing) => existing + totalTurnTokens);
        }
    }

    /// <inheritdoc />
    public BudgetExhaustion? CheckExhaustion(string? taskId, string? runId, string? stepId)
    {
        if (!string.IsNullOrEmpty(stepId) && _stepUsage.TryGetValue(stepId, out long stepTokens) && stepTokens >= DefaultStepBudgetLimit)
        {
            return new BudgetExhaustion("Step", $"Step '{stepId}' exceeded budget limit of {DefaultStepBudgetLimit:N0} tokens.", stepTokens, DefaultStepBudgetLimit, IsRecoverable: true);
        }

        if (!string.IsNullOrEmpty(runId) && _runUsage.TryGetValue(runId, out long runTokens) && runTokens >= DefaultRunBudgetLimit)
        {
            return new BudgetExhaustion("Run", $"Run '{runId}' exceeded run budget limit of {DefaultRunBudgetLimit:N0} tokens.", runTokens, DefaultRunBudgetLimit, IsRecoverable: true);
        }

        if (!string.IsNullOrEmpty(taskId) && _taskUsage.TryGetValue(taskId, out long taskTokens) && taskTokens >= DefaultTaskBudgetLimit)
        {
            return new BudgetExhaustion("Task", $"Task '{taskId}' exceeded total task budget limit of {DefaultTaskBudgetLimit:N0} tokens.", taskTokens, DefaultTaskBudgetLimit, IsRecoverable: false);
        }

        return null;
    }

    /// <inheritdoc />
    public void Reset(string? taskId, string? runId = null)
    {
        if (!string.IsNullOrEmpty(taskId)) _taskUsage.TryRemove(taskId, out _);
        if (!string.IsNullOrEmpty(runId)) _runUsage.TryRemove(runId, out _);
    }
}
