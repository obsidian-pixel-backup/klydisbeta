using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Tasks;

namespace Klydis.Core.Orchestration;

/// <summary>
/// Pure aggregator that turns durable ledger records into per-(model, protocol)
/// <see cref="ModelExecutionMetrics"/>. Deterministic and I/O-free so it is trivially
/// testable: feed it the same rows the store persisted and it produces the rates the
/// capability estimator consumes. The runtime facade (<c>AgentRuntime.BuildTelemetryAsync</c>)
/// supplies the raw rows; nothing here touches the database.
/// </summary>
public static class ExecutionTelemetryAnalyzer
{
    /// <summary>Model identity used when a row carries no model stamp (legacy rows).</summary>
    public const string UnknownModel = "unknown-model";

    /// <summary>Protocol identity used when a row carries no protocol stamp (legacy rows).</summary>
    public const string LegacyProtocol = "legacy";

    /// <summary>
    /// Groups the actions by (ModelId, ProtocolKey) and computes each group's metrics.
    /// <paramref name="stepKinds"/> maps step ids to their typed kind (from the persisted
    /// step mirror) so verification actions are identifiable; runs, when supplied, attribute
    /// run completion to the model that executed the most actions in that run.
    /// </summary>
    public static IReadOnlyList<ModelExecutionMetrics> AnalyzeAll(
        IReadOnlyList<TaskActionRecord> actions,
        IReadOnlyDictionary<string, StepActionKind>? stepKinds = null,
        IReadOnlyList<(string RunId, bool Completed)>? runOutcomes = null)
    {
        if (actions == null || actions.Count == 0) return Array.Empty<ModelExecutionMetrics>();

        var groups = actions
            .Where(a => a.Status != ActionExecutionStatus.Pending)
            .GroupBy(a => (Model: StampModel(a.ModelId), Protocol: StampProtocol(a.ProtocolKey)));
        var result = new List<ModelExecutionMetrics>(groups.Count());
        foreach (var group in groups)
        {
            // Pass the FULL ledger, not just this group's rows: run attribution must decide
            // which model dominated each run by counting ALL of that run's actions — a
            // model-filtered view would make every run look "dominant".
            result.Add(Analyze(
                group.Key.Model, group.Key.Protocol,
                actions, stepKinds, runOutcomes));
        }
        return result;
    }

    /// <summary>Computes the metrics for one model/protocol pair. <paramref name="actions"/>
    /// is the full ledger — the pair's own rows are selected internally, while run attribution
    /// counts every model's actions in each run.</summary>
    public static ModelExecutionMetrics Analyze(
        string modelId,
        string protocolKey,
        IReadOnlyList<TaskActionRecord> actions,
        IReadOnlyDictionary<string, StepActionKind>? stepKinds = null,
        IReadOnlyList<(string RunId, bool Completed)>? runOutcomes = null)
    {
        if (actions == null) throw new ArgumentNullException(nameof(actions));

        var mine = actions
            .Where(a =>
                string.Equals(StampModel(a.ModelId), modelId, StringComparison.Ordinal) &&
                string.Equals(StampProtocol(a.ProtocolKey), protocolKey, StringComparison.Ordinal))
            .ToList();

        var terminal = mine
            .Where(a => a.Status is ActionExecutionStatus.Succeeded or
                        ActionExecutionStatus.Failed or
                        ActionExecutionStatus.TimedOut or
                        ActionExecutionStatus.Unknown)
            .ToList();

        int succeeded = terminal.Count(a => a.Status == ActionExecutionStatus.Succeeded);
        int failed = terminal.Count(a => a.Status == ActionExecutionStatus.Failed);
        int timedOut = terminal.Count(a => a.Status == ActionExecutionStatus.TimedOut);
        int unknown = terminal.Count(a => a.Status == ActionExecutionStatus.Unknown);
        int cancelled = mine.Count(a => a.Status == ActionExecutionStatus.Cancelled);

        // First-action-per-step: the EARLIEST terminal action of each step decides whether
        // the model "got the step right the first time". Steps with no terminal action
        // (cancelled/in-flight only) are excluded — there is no outcome to judge.
        var stepOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstOutcomes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var action in terminal
                     .Where(a => !string.IsNullOrEmpty(a.StepId))
                     .OrderBy(a => a.StartedAtUtc)
                     .ThenBy(a => a.ActionId, StringComparer.Ordinal))
        {
            var stepId = action.StepId!;
            if (!stepOrder.TryGetValue(stepId, out var order))
            {
                stepOrder[stepId] = order = stepOrder.Count;
            }
            if (firstOutcomes.ContainsKey(stepId)) continue;
            firstOutcomes[stepId] = action.Status == ActionExecutionStatus.Succeeded;
        }

        // Per-step action counts for the repair proxy (steps with ≥2 actions needed churn).
        var stepActionCounts = terminal
            .Where(a => !string.IsNullOrEmpty(a.StepId))
            .GroupBy(a => a.StepId!)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Verification actions: those whose step kind is Verification (typed step mirror).
        var verificationActions = terminal.Where(a =>
            !string.IsNullOrEmpty(a.StepId) &&
            stepKinds != null &&
            stepKinds.TryGetValue(a.StepId!, out var kind) &&
            kind == StepActionKind.Verification).ToList();

        // File-mutation actions: the coding signal (write/edit — the tools that actually
        // produce project code and documents).
        var fileMutationActions = terminal.Where(a =>
            a.ToolName is not null &&
            (a.ToolName.Equals("write_file", StringComparison.OrdinalIgnoreCase) ||
             a.ToolName.Equals("edit_file", StringComparison.OrdinalIgnoreCase))).ToList();

        // Run attribution: a run's outcome belongs to the model that executed the most
        // actions in it — computed over the FULL ledger so the dominant model is judged
        // against every action of the run, not just this pair's rows.
        int runsAttributed = 0;
        int runsCompleted = 0;
        if (runOutcomes != null && runOutcomes.Count > 0)
        {
            var runsOfModel = actions
                .Where(a => a.RunId != null)
                .GroupBy(a => a.RunId!)
                .Select(g => (RunId: g.Key, DominantModel: g
                    .GroupBy(a => StampModel(a.ModelId))
                    .OrderByDescending(m => m.Count())
                    .First().Key));
            foreach (var (runId, dominantModel) in runsOfModel)
            {
                if (!string.Equals(dominantModel, modelId, StringComparison.Ordinal)) continue;
                foreach (var (candidateRunId, completed) in runOutcomes)
                {
                    if (!string.Equals(candidateRunId, runId, StringComparison.Ordinal)) continue;
                    runsAttributed++;
                    if (completed) runsCompleted++;
                    break;
                }
            }
        }

        return new ModelExecutionMetrics(
            ModelId: modelId,
            ProtocolKey: protocolKey,
            TotalActions: mine.Count,
            SucceededActions: succeeded,
            FailedActions: failed,
            TimedOutActions: timedOut,
            CancelledActions: cancelled,
            UnknownActions: unknown,
            StepsTouched: stepOrder.Count,
            FirstActionsSucceeded: firstOutcomes.Values.Count(v => v),
            FirstActionsAttempted: firstOutcomes.Count,
            StepsWithMultipleActions: stepActionCounts.Values.Count(c => c >= 2),
            StepsWithAnyAction: stepActionCounts.Count,
            VerificationActionsSucceeded: verificationActions.Count(a => a.Status == ActionExecutionStatus.Succeeded),
            VerificationActionsAttempted: verificationActions.Count,
            FileMutationActionsSucceeded: fileMutationActions.Count(a => a.Status == ActionExecutionStatus.Succeeded),
            FileMutationActionsAttempted: fileMutationActions.Count,
            RunsAttributed: runsAttributed,
            RunsCompleted: runsCompleted);
    }

    /// <summary>Aggregates several metric batches for the same (model, protocol) — e.g. a
    /// merged profile across sessions — by summing the underlying counters.</summary>
    public static ModelExecutionMetrics Merge(IEnumerable<ModelExecutionMetrics> batches)
    {
        var all = batches?.Where(m => m != null).ToList() ?? new List<ModelExecutionMetrics>();
        if (all.Count == 0) throw new ArgumentException("At least one batch is required.", nameof(batches));
        if (all.Count == 1) return all[0];

        var first = all[0];
        return new ModelExecutionMetrics(
            ModelId: first.ModelId,
            ProtocolKey: first.ProtocolKey,
            TotalActions: all.Sum(m => m.TotalActions),
            SucceededActions: all.Sum(m => m.SucceededActions),
            FailedActions: all.Sum(m => m.FailedActions),
            TimedOutActions: all.Sum(m => m.TimedOutActions),
            CancelledActions: all.Sum(m => m.CancelledActions),
            UnknownActions: all.Sum(m => m.UnknownActions),
            StepsTouched: all.Sum(m => m.StepsTouched),
            FirstActionsSucceeded: all.Sum(m => m.FirstActionsSucceeded),
            FirstActionsAttempted: all.Sum(m => m.FirstActionsAttempted),
            StepsWithMultipleActions: all.Sum(m => m.StepsWithMultipleActions),
            StepsWithAnyAction: all.Sum(m => m.StepsWithAnyAction),
            VerificationActionsSucceeded: all.Sum(m => m.VerificationActionsSucceeded),
            VerificationActionsAttempted: all.Sum(m => m.VerificationActionsAttempted),
            FileMutationActionsSucceeded: all.Sum(m => m.FileMutationActionsSucceeded),
            FileMutationActionsAttempted: all.Sum(m => m.FileMutationActionsAttempted),
            RunsAttributed: all.Sum(m => m.RunsAttributed),
            RunsCompleted: all.Sum(m => m.RunsCompleted));
    }

    internal static string StampModel(string? modelId)
        => string.IsNullOrWhiteSpace(modelId) ? UnknownModel : modelId;

    internal static string StampProtocol(string? protocolKey)
        => string.IsNullOrWhiteSpace(protocolKey) ? LegacyProtocol : protocolKey;
}
