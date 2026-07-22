using System;

namespace Klydis.Core.Inference.Telemetry;

/// <summary>
/// Detailed telemetry captured for a single inference generation request.
/// </summary>
public record InferenceTelemetry(
    string RequestId,
    string TargetModelPath,
    string? DraftModelPath,
    bool IsSpeculativeEnabled,
    int PromptLengthChars,
    int PromptTokenCount,
    int GeneratedTokenCount,
    double TimeToFirstTokenMs,
    double GenerationDurationMs,
    double TotalElapsedMs,
    double GenerationTokensPerSecond,
    double EndToEndTokensPerSecond,
    SpeculativeTelemetry? SpeculativeMetrics
);
