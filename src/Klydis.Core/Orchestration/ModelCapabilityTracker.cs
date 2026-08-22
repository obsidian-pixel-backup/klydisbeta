using System;
using System.Collections.Concurrent;

namespace Klydis.Core.Orchestration;

/// <summary>
/// Machine taxonomy for agent and tool failures.
/// </summary>
public enum SystemErrorTaxonomy
{
    None = 0,
    ModelError = 1,
    HarnessError = 2,
    ToolError = 3,
    EnvironmentError = 4,
    EpistemicRejection = 5
}

/// <summary>
/// Online model capability tracker (P1).
/// Measures real empirical reliability of local LLMs across tool emission, syntax validity,
/// execution success, evidence authority, and progress velocity.
/// </summary>
public sealed class ModelCapabilityTracker
{
    private sealed class ModelScoreRecord
    {
        public int EmittedCalls;
        public int ValidSyntaxCalls;
        public int ValidArgCalls;
        public int SuccessfulExecutions;
        public int AuthoritativeEvidenceCount;
        public int HallucinatedEvidenceCount;
        public int SuccessfulRepairs;
        public int RepairAttempts;
    }

    private readonly ConcurrentDictionary<string, ModelScoreRecord> _modelStats = new(StringComparer.OrdinalIgnoreCase);

    public void RecordToolEmission(string modelId, bool syntaxValid, bool argsValid)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var record = _modelStats.GetOrAdd(modelId, _ => new ModelScoreRecord());
        lock (record)
        {
            record.EmittedCalls++;
            if (syntaxValid) record.ValidSyntaxCalls++;
            if (argsValid) record.ValidArgCalls++;
        }
    }

    public void RecordExecutionOutcome(string modelId, bool executionSuccess, bool evidenceAuthoritative, bool isHallucinated)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var record = _modelStats.GetOrAdd(modelId, _ => new ModelScoreRecord());
        lock (record)
        {
            if (executionSuccess) record.SuccessfulExecutions++;
            if (evidenceAuthoritative) record.AuthoritativeEvidenceCount++;
            if (isHallucinated) record.HallucinatedEvidenceCount++;
        }
    }

    public void RecordRepairAttempt(string modelId, bool success)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var record = _modelStats.GetOrAdd(modelId, _ => new ModelScoreRecord());
        lock (record)
        {
            record.RepairAttempts++;
            if (success) record.SuccessfulRepairs++;
        }
    }

    /// <summary>
    /// Calculates the empirical dynamic protocol confidence (0..1) for a model based on execution history.
    /// </summary>
    public double CalculateDynamicConfidence(string modelId, double defaultPrior = 0.40)
    {
        if (!_modelStats.TryGetValue(modelId, out var record) || record.EmittedCalls == 0)
        {
            return defaultPrior;
        }

        lock (record)
        {
            double syntaxRate = (double)record.ValidSyntaxCalls / record.EmittedCalls;
            double argRate = (double)record.ValidArgCalls / record.EmittedCalls;
            double execRate = record.EmittedCalls > 0 ? (double)record.SuccessfulExecutions / record.EmittedCalls : 0.0;
            double repairRate = record.RepairAttempts > 0 ? (double)record.SuccessfulRepairs / record.RepairAttempts : 0.5;

            double measured = (syntaxRate * 0.35) + (argRate * 0.25) + (execRate * 0.25) + (repairRate * 0.15);

            // Bayesian blend with prior (weight = 5 pseudo observations)
            double w = 5.0;
            double n = record.EmittedCalls;
            return Math.Clamp((defaultPrior * w + measured * n) / (w + n), 0.05, 0.98);
        }
    }
}
