using System;

namespace Klydis.Core.Inference.Telemetry;

/// <summary>
/// Detailed telemetry captured for a single inference generation request.
/// </summary>
public record InferenceTelemetry(
    string RequestId = "",
    string TargetModelPath = "",
    string? DraftModelPath = null,
    bool IsSpeculativeEnabled = false,
    int PromptLengthChars = 0,
    int PromptTokenCount = 0,
    int GeneratedTokenCount = 0,
    double TimeToFirstTokenMs = 0,
    double GenerationDurationMs = 0,
    double TotalElapsedMs = 0,
    double GenerationTokensPerSecond = 0,
    double EndToEndTokensPerSecond = 0,
    SpeculativeTelemetry? SpeculativeMetrics = null,
    bool IsIsolated = false
);
