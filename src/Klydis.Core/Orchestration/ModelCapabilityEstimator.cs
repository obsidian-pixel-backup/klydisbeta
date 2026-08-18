using System;

namespace Klydis.Core.Orchestration;

/// <summary>
/// Turns raw <see cref="ModelExecutionMetrics"/> into a smoothed <see cref="ModelCapabilityProfile"/>.
/// Each observed rate is blended with its prior using a Bayesian weight of
/// <see cref="PriorObservationWeight"/> pseudo-observations, so:
///  - a model with NO history gets exactly its prior (conservative, honest);
///  - one or two samples barely move the profile (a single success is not a capability);
///  - tens of samples converge to the measured reality (the model's profile becomes its
///    actual execution record, not the family's stereotype).
/// The same estimator also produces the profile from a <see cref="Klydis.Core.Protocol.ModelProfile"/>
/// alone (no metrics) — the capability-probe path — so both inputs produce the same record type.
/// </summary>
public static class ModelCapabilityEstimator
{
    /// <summary>
    /// Pseudo-observation weight of the prior: with 8 prior observations, a 20-sample run
    /// shifts the profile ~70% toward the measured rate — enough history to dominate, too
    /// little to let a lucky streak masquerade as capability.
    /// </summary>
    public const double PriorObservationWeight = 8.0;

    /// <summary>Builds a prior from a model profile's family knowledge.</summary>
    public static ModelCapabilityPrior PriorFromProfile(Klydis.Core.Protocol.ModelProfile profile)
    {
        if (profile == null) return ModelCapabilityPrior.Default;
        return new ModelCapabilityPrior(
            Reasoning: 0.5,
            Coding: 0.5,
            ToolReliability: ModelCapabilityPrior.FromLevel(profile.ToolCalling),
            RepairAbility: ModelCapabilityPrior.FromLevel(profile.Repair),
            InstructionFollowing: ModelCapabilityPrior.FromLevel(profile.Continuation),
            VerificationReliability: 0.45,
            CompletionReliability: 0.5,
            ProtocolConfidence: Math.Clamp(profile.ProtocolConfidence, 0.0, 1.0));
    }

    /// <summary>
    /// Estimates the profile from execution metrics (or null metrics = prior only).
    /// <paramref name="prior"/> defaults to the conservative prior when the caller has no
    /// richer model knowledge.
    /// </summary>
    public static ModelCapabilityProfile Estimate(
        string modelId,
        string protocolKey,
        ModelExecutionMetrics? metrics,
        ModelCapabilityPrior? prior = null)
    {
        var p = prior ?? ModelCapabilityPrior.Default;
        if (metrics == null)
        {
            return new ModelCapabilityProfile(
                ModelId: modelId,
                ProtocolKey: protocolKey,
                SampleCount: 0,
                Reasoning: p.Reasoning,
                Coding: p.Coding,
                ToolReliability: p.ToolReliability,
                RepairAbility: p.RepairAbility,
                InstructionFollowing: p.InstructionFollowing,
                VerificationReliability: p.VerificationReliability,
                CompletionReliability: p.CompletionReliability,
                ProtocolConfidence: p.ProtocolConfidence);
        }

        double n = metrics.SampleCount;
        double w = PriorObservationWeight;

        // Observed signals per dimension. Repair is the inverse of the repair rate (a model
        // that rarely needs a second action repairs well); first-action success and
        // completion reliability back each other up.
        double observedReasoning = metrics.FirstActionSuccessRate;
        double observedCoding = metrics.FileMutationSuccessRate;
        double observedTools = metrics.ToolSuccessRate;
        double observedRepair = 1.0 - metrics.RepairRate;
        double observedFollowing = metrics.FirstActionSuccessRate;
        double observedVerification = metrics.VerificationSuccessRate;
        double observedCompletion = metrics.CompletionRate;

        double Blend(double priorValue, double observed) => (priorValue * w + observed * n) / (w + n);

        // Protocol confidence blends the prior confidence with the observed first-action
        // success — a model that keeps failing its very first action is evidence the
        // protocol pairing is wrong, not just the step.
        double protocolConfidence = Blend(p.ProtocolConfidence, metrics.FirstActionSuccessRate);

        return new ModelCapabilityProfile(
            ModelId: modelId,
            ProtocolKey: protocolKey,
            SampleCount: metrics.SampleCount,
            Reasoning: Blend(p.Reasoning, observedReasoning),
            Coding: Blend(p.Coding, observedCoding),
            ToolReliability: Blend(p.ToolReliability, observedTools),
            RepairAbility: Blend(p.RepairAbility, observedRepair),
            InstructionFollowing: Blend(p.InstructionFollowing, observedFollowing),
            VerificationReliability: Blend(p.VerificationReliability, observedVerification),
            CompletionReliability: Blend(p.CompletionReliability, observedCompletion),
            ProtocolConfidence: protocolConfidence);
    }

    /// <summary>Convenience overload for the profile-only (no execution history) path.</summary>
    public static ModelCapabilityProfile FromProfile(Klydis.Core.Protocol.ModelProfile profile)
        => Estimate(
            profile.ModelId,
            Klydis.Core.Protocol.ProtocolRegistry.ResolveProtocolKey(profile) ?? ExecutionTelemetryAnalyzer.LegacyProtocol,
            metrics: null,
            prior: PriorFromProfile(profile));
}
