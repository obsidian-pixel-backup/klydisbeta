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
    bool IsIsolated = false,

    /// <summary>
    /// Prompt tokens processed per second during the prefill phase (prompt token count
    /// divided by time-to-first-token). Estimated: TTFT includes the first decode, so this
    /// slightly understates true prefill throughput; it is still the only prefill signal
    /// available without native llama_perf hooks and lets the UI track TTFT regressions.
    /// </summary>
    double PromptPrefillTokensPerSecond = 0
);
