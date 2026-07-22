using System;

namespace Klydis.Core.Inference.Telemetry;

/// <summary>
/// Telemetry captured specifically for speculative draft generation and verification.
/// </summary>
public record SpeculativeTelemetry(
    int TotalDraftTokensSpeculated,
    int TotalDraftTokensAccepted,
    int TotalVerificationSteps,
    int TotalRejections,
    int TotalFallbackSteps,
    double DraftAcceptanceRate,
    double MeanAcceptedTokensPerStep
)
{
    /// <summary>
    /// Calculates a <see cref="SpeculativeTelemetry"/> instance from raw speculation counts.
    /// </summary>
    public static SpeculativeTelemetry Calculate(
        int speculated, int accepted, int steps, int rejections, int fallbacks)
    {
        double alpha = speculated > 0 ? (double)accepted / speculated : 0.0;
        double mu = steps > 0 ? (double)accepted / steps : 0.0;
        return new SpeculativeTelemetry(speculated, accepted, steps, rejections, fallbacks, alpha, mu);
    }
}
