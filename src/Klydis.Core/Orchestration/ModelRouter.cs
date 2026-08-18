using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Tasks;

namespace Klydis.Core.Orchestration;

/// <summary>
/// What the router is being asked to select a model FOR — a step's <see cref="StepActionKind"/>.
/// The step kind is the contract: a Verification step needs a model that reliably drives
/// verification, an Implementation step needs one that produces working files, a Reason step
/// needs one that thinks before acting.
/// </summary>
public sealed record ModelRouteRequest(
    StepActionKind Kind,
    IReadOnlyList<string>? RequiredSkills = null,
    IReadOnlyList<string>? PreferredModelIds = null,
    bool PreferLoaded = false,
    double? MaxCostPerToken = null,
    double? MaxLatency = null)
{
    /// <summary>True when the step actually needs tools (vs. text-only steps).</summary>
    public bool RequiresTools
        => Kind is StepActionKind.Inspect or
           StepActionKind.Research or
           StepActionKind.FileMutation or
           StepActionKind.CommandExecution or
           StepActionKind.TerminalInteraction or
           StepActionKind.Verification;
}

/// <summary>One routable model: its measured capability profile plus operational facts.</summary>
public sealed record ModelCandidate(
    string ModelId,
    ModelCapabilityProfile Capability,
    bool Loaded = false,
    double? CostPerToken = null,
    double? Latency = null);

/// <summary>One ranked candidate with its score breakdown.</summary>
public sealed record ModelRouteRanking(
    string ModelId,
    double Score,
    double PrimaryCapability,
    string Reason);

/// <summary>
/// The router's decision: the best model for the step (or null when nothing is routable),
/// the full ranking, and flags that matter to the caller — a LOW protocol confidence on a
/// tool-requiring step means "use this model but with the conservative execution policy
/// (one tool per turn, more repair budget)".
/// </summary>
public sealed record ModelRouteRecommendation(
    string? BestModelId,
    IReadOnlyList<ModelRouteRanking> Rankings,
    bool LowConfidenceProtocol,
    bool PreferredHonored,
    string Reason);

/// <summary>
/// Routes a step to the best available model (agent-intelligence stage §4). Scores every
/// candidate by how its MEASURED capability matches the step kind — not by family
/// stereotypes — plus protocol confidence and completion reliability, with optional cost /
/// latency / loaded penalties. Pure and deterministic: no I/O, no randomness, so routing is
/// unit-testable and reproducible.
/// </summary>
public static class ModelRouter
{
    // Score weights: the step's primary capability dominates; tool reliability and protocol
    // confidence matter for every step (a brilliant reasoner that cannot speak the protocol
    // is useless in the loop); completion reliability favors models that finish.
    private const double PrimaryWeight = 0.5;
    private const double ToolReliabilityWeight = 0.2;
    private const double ProtocolConfidenceWeight = 0.15;
    private const double CompletionReliabilityWeight = 0.15;
    private const double CostPenaltyWeight = 0.10;
    private const double LatencyPenaltyWeight = 0.10;
    private const double LoadedBonus = 0.05;
    private const double PreferredBonus = 0.15;

    /// <summary>Below this protocol confidence, a tool-requiring step flags
    /// <see cref="ModelRouteRecommendation.LowConfidenceProtocol"/> (the caller should run the
    /// conservative execution policy for that step).</summary>
    public const double LowProtocolConfidenceThreshold = 0.3;

    /// <summary>Which capability dimension a step kind is primarily judged on.</summary>
    public static double PrimaryCapability(ModelCapabilityProfile profile, StepActionKind kind)
        => kind switch
        {
            StepActionKind.Reason => profile.Reasoning,
            StepActionKind.Research => profile.Reasoning,
            StepActionKind.Plan => profile.Reasoning,
            StepActionKind.Inspect => profile.ToolReliability,
            StepActionKind.FileMutation => profile.Coding,
            StepActionKind.CommandExecution => profile.ToolReliability,
            StepActionKind.TerminalInteraction => profile.ToolReliability,
            StepActionKind.Verification => profile.VerificationReliability,
            StepActionKind.UserInput => profile.InstructionFollowing,
            StepActionKind.Summary => profile.Coding,
            _ => profile.ToolReliability
        };

    /// <summary>Ranks every candidate for the step and returns the best (or null when there
    /// are no candidates).</summary>
    public static ModelRouteRecommendation RouteStep(
        ModelRouteRequest request,
        IReadOnlyList<ModelCandidate> candidates)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (candidates == null || candidates.Count == 0)
        {
            return new ModelRouteRecommendation(
                BestModelId: null,
                Rankings: Array.Empty<ModelRouteRanking>(),
                LowConfidenceProtocol: false,
                PreferredHonored: false,
                Reason: "No model candidates available to route.");
        }

        var preferred = request.PreferredModelIds?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        var rankings = candidates
            .Select(c =>
            {
                var cap = c.Capability;
                double primary = PrimaryCapability(cap, request.Kind);
                double score =
                    PrimaryWeight * primary +
                    ToolReliabilityWeight * cap.ToolReliability +
                    ProtocolConfidenceWeight * cap.ProtocolConfidence +
                    CompletionReliabilityWeight * cap.CompletionReliability;

                var penalties = new List<string>();
                if (request.MaxCostPerToken.HasValue && c.CostPerToken.HasValue &&
                    c.CostPerToken.Value > request.MaxCostPerToken.Value)
                {
                    score -= CostPenaltyWeight * c.CostPerToken.Value;
                    penalties.Add($"cost {c.CostPerToken.Value:0.00}");
                }
                if (request.MaxLatency.HasValue && c.Latency.HasValue &&
                    c.Latency.Value > request.MaxLatency.Value)
                {
                    score -= LatencyPenaltyWeight * c.Latency.Value;
                    penalties.Add($"latency {c.Latency.Value:0.00}");
                }

                bool preferredPick = preferred.Contains(c.ModelId);
                if (preferredPick) score += PreferredBonus;
                if (request.PreferLoaded && c.Loaded) score += LoadedBonus;

                string reason = BuildReason(c, request.Kind, primary, preferredPick, penalties);
                return new ModelRouteRanking(c.ModelId, score, primary, reason);
            })
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => PreferredOrLoadedRank(r, preferred))
            .ThenBy(r => r.ModelId, StringComparer.Ordinal)
            .ToList();

        var best = rankings[0];
        bool lowConfidence = request.RequiresTools &&
                             best.Score > 0 &&
                             CandidateFor(best.ModelId, candidates).Capability.ProtocolConfidence < LowProtocolConfidenceThreshold;
        bool preferredHonored = preferred.Count > 0 &&
                                rankings.Any(r => preferred.Contains(r.ModelId) && r == rankings[0]);

        return new ModelRouteRecommendation(
            BestModelId: best.ModelId,
            Rankings: rankings,
            LowConfidenceProtocol: lowConfidence,
            PreferredHonored: preferredHonored,
            Reason: BuildDecisionReason(request, best, lowConfidence, preferredHonored));
    }

    /// <summary>Convenience overload building the request from a step kind directly.</summary>
    public static ModelRouteRecommendation RouteStep(
        StepActionKind kind,
        IReadOnlyList<ModelCandidate> candidates,
        IReadOnlyList<string>? preferredModelIds = null,
        bool preferLoaded = false)
        => RouteStep(new ModelRouteRequest(kind, PreferredModelIds: preferredModelIds, PreferLoaded: preferLoaded), candidates);

    private static ModelCandidate CandidateFor(string modelId, IReadOnlyList<ModelCandidate> candidates)
        => candidates.First(c => string.Equals(c.ModelId, modelId, StringComparison.Ordinal));

    private static int PreferredOrLoadedRank(ModelRouteRanking ranking, HashSet<string> preferred)
        => (preferred.Contains(ranking.ModelId) ? 1 : 0);

    private static string BuildReason(
        ModelCandidate candidate,
        StepActionKind kind,
        double primary,
        bool preferredPick,
        List<string> penalties)
    {
        var parts = new List<string>
        {
            $"{kind} capability {primary:0.00}",
            $"tools {candidate.Capability.ToolReliability:0.00}",
            $"protocol {candidate.Capability.ProtocolConfidence:0.00}",
            $"completion {candidate.Capability.CompletionReliability:0.00}"
        };
        if (preferredPick) parts.Add("user-preferred");
        if (candidate.Loaded) parts.Add("loaded");
        parts.AddRange(penalties);
        return string.Join(", ", parts);
    }

    private static string BuildDecisionReason(
        ModelRouteRequest request,
        ModelRouteRanking best,
        bool lowConfidence,
        bool preferredHonored)
    {
        var parts = new List<string>
        {
            $"best model '{best.ModelId}' for {request.Kind} step (score {best.Score:0.00})"
        };
        if (lowConfidence)
        {
            parts.Add("WARNING: protocol confidence is low for a tool-requiring step — use the conservative execution policy");
        }
        if (preferredHonored) parts.Add("user preference honored");
        return string.Join("; ", parts);
    }
}
