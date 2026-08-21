using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// Quality assessment result for a model-generated execution plan.
/// </summary>
public sealed record PlanQualityResult(
    bool IsAcceptable,
    double GenericScore,
    string? RejectionReason,
    IReadOnlyList<string> QualityFeedback)
{
    public static PlanQualityResult Pass(double genericScore = 0.0, IReadOnlyList<string>? feedback = null)
        => new(true, genericScore, null, feedback ?? Array.Empty<string>());

    public static PlanQualityResult Reject(string reason, double genericScore = 1.0, IReadOnlyList<string>? feedback = null)
        => new(false, genericScore, reason, feedback ?? Array.Empty<string>());
}

/// <summary>
/// Telemetry metrics for plan entropy and diversity across generated plans.
/// </summary>
public sealed record PlanEntropyMetrics(
    double PlanDiversity,
    double TemplateReuseRate,
    double GenericTaskRatio,
    int TotalPlansEvaluated);

/// <summary>
/// Evaluates the substance and quality of model-generated plans.
/// Detects generic workflow templates (e.g. "Analyze -> Research -> Implement -> Test -> Review -> Complete")
/// and maintains plan entropy metrics.
/// </summary>
public sealed class PlanQualityValidator
{
    private static readonly string[] GenericKeywords =
    {
        "analyze", "research", "implement", "test", "review", "complete",
        "analysis", "implementation", "testing", "verification", "summary",
        "investigate", "execute", "finalize", "deliver", "scaffold"
    };

    private readonly ConcurrentQueue<ExecutionPlan> _recentPlans = new();
    private const int MaxHistorySize = 50;

    /// <summary>
    /// Evaluates whether an execution plan is substantive and specific to the objective,
    /// rather than a generic boilerplate workflow.
    /// </summary>
    public PlanQualityResult Evaluate(ExecutionPlan plan)
    {
        if (plan == null) return PlanQualityResult.Reject("Plan is null.");

        // An empty plan (0 tasks) at task creation is allowed as the initial state before model planning.
        if (plan.Tasks == null || plan.Tasks.Count == 0)
        {
            return PlanQualityResult.Pass(0.0);
        }

        var feedback = new List<string>();
        int genericTaskCount = 0;

        foreach (var task in plan.Tasks)
        {
            if (IsGenericTaskDescription(task.Description))
            {
                genericTaskCount++;
            }
        }

        double genericRatio = (double)genericTaskCount / plan.Tasks.Count;

        // If >= 75% of tasks in a 4+ task plan are single-word or generic boilerplate verbs
        if (plan.Tasks.Count >= 3 && genericRatio >= 0.75 && IsBoilerplateSequence(plan.Tasks))
        {
            return PlanQualityResult.Reject(
                "GENERIC_PLAN_DETECTED: The plan uses a generic workflow template (e.g., Analyze -> Research -> Implement -> Test) " +
                "instead of tasks specific to the objective. Regenerate an execution plan with concrete tasks materially contributing to the goal.",
                genericRatio,
                feedback);
        }

        // Track plan in history
        _recentPlans.Enqueue(plan);
        while (_recentPlans.Count > MaxHistorySize && _recentPlans.TryDequeue(out _)) { }

        return PlanQualityResult.Pass(genericRatio, feedback);
    }

    /// <summary>
    /// Computes entropy and diversity metrics across recently evaluated plans.
    /// </summary>
    public PlanEntropyMetrics GetMetrics()
    {
        var plans = _recentPlans.ToArray();
        if (plans.Length <= 1)
        {
            return new PlanEntropyMetrics(1.0, 0.0, 0.0, plans.Length);
        }

        int totalTasks = 0;
        int genericTasks = 0;
        double totalPairwiseSimilarity = 0.0;
        int comparisonCount = 0;

        for (int i = 0; i < plans.Length; i++)
        {
            totalTasks += plans[i].Tasks.Count;
            genericTasks += plans[i].Tasks.Count(t => IsGenericTaskDescription(t.Description));

            for (int j = i + 1; j < plans.Length; j++)
            {
                totalPairwiseSimilarity += CalculatePlanSimilarity(plans[i], plans[j]);
                comparisonCount++;
            }
        }

        double avgSimilarity = comparisonCount > 0 ? totalPairwiseSimilarity / comparisonCount : 0.0;
        double diversity = Math.Clamp(1.0 - avgSimilarity, 0.0, 1.0);
        double templateReuse = Math.Clamp(avgSimilarity, 0.0, 1.0);
        double genericRatio = totalTasks > 0 ? (double)genericTasks / totalTasks : 0.0;

        return new PlanEntropyMetrics(diversity, templateReuse, genericRatio, plans.Length);
    }

    private static bool IsGenericTaskDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return true;
        string trimmed = description.Trim().ToLowerInvariant();
        var words = trimmed.Split(new[] { ' ', '-', '_', '.', ':', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 2 && GenericKeywords.Any(k => words.Contains(k)))
        {
            return true;
        }
        return false;
    }

    private static bool IsBoilerplateSequence(IReadOnlyList<PlanTask> tasks)
    {
        // Check if task descriptions match the classic 4-6 step boilerplate sequence
        var words = tasks.Select(t => t.Description.Trim().ToLowerInvariant()).ToList();
        int matchedKeywords = 0;
        foreach (var w in words)
        {
            if (GenericKeywords.Any(k => w.Equals(k, StringComparison.OrdinalIgnoreCase) || w.StartsWith(k + " ", StringComparison.OrdinalIgnoreCase)))
            {
                matchedKeywords++;
            }
        }
        return (double)matchedKeywords / tasks.Count >= 0.7;
    }

    /// <summary>
    /// Jaccard similarity between two plans based on normalized task tokens.
    /// </summary>
    public static double CalculatePlanSimilarity(ExecutionPlan a, ExecutionPlan b)
    {
        if (a == null || b == null) return 0.0;
        if (a.Tasks.Count == 0 && b.Tasks.Count == 0) return 1.0;
        if (a.Tasks.Count == 0 || b.Tasks.Count == 0) return 0.0;

        var setA = new HashSet<string>(a.Tasks.SelectMany(t => Tokenize(t.Description)), StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b.Tasks.SelectMany(t => Tokenize(t.Description)), StringComparer.OrdinalIgnoreCase);

        if (setA.Count == 0 && setB.Count == 0) return 1.0;
        int intersection = setA.Count(setB.Contains);
        int union = setA.Union(setB).Count();
        return union > 0 ? (double)intersection / union : 0.0;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Empty<string>();
        return text.ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '(', ')', '[', ']', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
