using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Inference.Telemetry;

namespace Klydis.Core.Benchmarking;

/// <summary>
/// Detailed benchmark results for a single workload prompt profile.
/// </summary>
public record WorkloadBenchmarkResult(
    BenchmarkPromptProfile Profile,
    MetricDistribution BaselineGenTokSec,
    MetricDistribution BaselineTtftMs,
    MetricDistribution BaselineE2ETokSec,
    MetricDistribution? SpeculativeGenTokSec,
    MetricDistribution? SpeculativeTtftMs,
    MetricDistribution? SpeculativeE2ETokSec,
    SpeculativeTelemetry? SpeculativeTelemetry,
    double SpeedupRatio,
    List<InferenceTelemetry> BaselineRawTelemetry,
    List<InferenceTelemetry> SpeculativeRawTelemetry
);

/// <summary>
/// Executive summary of comparative benchmark results across all workloads.
/// </summary>
public record BenchmarkSummary(
    double BaselineGenTokSec,
    double SpeculativeGenTokSec,
    double SpeedupRatio,
    double DraftAcceptanceRate,
    double BaselineTtftMs,
    double SpeculativeTtftMs,
    double BaselineE2ETokSec,
    double SpeculativeE2ETokSec,
    int TotalDraftTokensSpeculated,
    int TotalDraftTokensAccepted,
    int TotalVerificationSteps,
    double MeanAcceptedTokensPerStep,
    int TotalRejections
);

/// <summary>
/// Top-level comparative benchmark result capturing baseline vs. speculative performance.
/// </summary>
public record ComparativeBenchmarkResult(
    DateTime TimestampUtc,
    BenchmarkConfig Config,
    BenchmarkSummary Summary,
    List<WorkloadBenchmarkResult> WorkloadResults
)
{
    /// <summary>
    /// Creates a <see cref="ComparativeBenchmarkResult"/> by aggregating workload benchmark results.
    /// </summary>
    public static ComparativeBenchmarkResult Aggregate(
        BenchmarkConfig config,
        List<WorkloadBenchmarkResult> workloadResults)
    {
        double avgBaselineGen = workloadResults.Count > 0 ? workloadResults.Average(w => w.BaselineGenTokSec.Mean) : 0;
        double avgSpecGen = workloadResults.Count > 0 && workloadResults.Any(w => w.SpeculativeGenTokSec != null)
            ? workloadResults.Where(w => w.SpeculativeGenTokSec != null).Average(w => w.SpeculativeGenTokSec!.Mean)
            : avgBaselineGen;

        double speedup = avgBaselineGen > 0 ? avgSpecGen / avgBaselineGen : 1.0;

        double avgBaselineTtft = workloadResults.Count > 0 ? workloadResults.Average(w => w.BaselineTtftMs.Mean) : 0;
        double avgSpecTtft = workloadResults.Count > 0 && workloadResults.Any(w => w.SpeculativeTtftMs != null)
            ? workloadResults.Where(w => w.SpeculativeTtftMs != null).Average(w => w.SpeculativeTtftMs!.Mean)
            : avgBaselineTtft;

        double avgBaselineE2E = workloadResults.Count > 0 ? workloadResults.Average(w => w.BaselineE2ETokSec.Mean) : 0;
        double avgSpecE2E = workloadResults.Count > 0 && workloadResults.Any(w => w.SpeculativeE2ETokSec != null)
            ? workloadResults.Where(w => w.SpeculativeE2ETokSec != null).Average(w => w.SpeculativeE2ETokSec!.Mean)
            : avgBaselineE2E;

        int totalSpeculated = workloadResults.Sum(w => w.SpeculativeTelemetry?.TotalDraftTokensSpeculated ?? 0);
        int totalAccepted = workloadResults.Sum(w => w.SpeculativeTelemetry?.TotalDraftTokensAccepted ?? 0);
        int totalSteps = workloadResults.Sum(w => w.SpeculativeTelemetry?.TotalVerificationSteps ?? 0);
        int totalRejections = workloadResults.Sum(w => w.SpeculativeTelemetry?.TotalRejections ?? 0);

        double alpha = totalSpeculated > 0 ? (double)totalAccepted / totalSpeculated : 0.0;
        double mu = totalSteps > 0 ? (double)totalAccepted / totalSteps : 0.0;

        var summary = new BenchmarkSummary(
            BaselineGenTokSec: Math.Round(avgBaselineGen, 2),
            SpeculativeGenTokSec: Math.Round(avgSpecGen, 2),
            SpeedupRatio: Math.Round(speedup, 2),
            DraftAcceptanceRate: Math.Round(alpha, 4),
            BaselineTtftMs: Math.Round(avgBaselineTtft, 2),
            SpeculativeTtftMs: Math.Round(avgSpecTtft, 2),
            BaselineE2ETokSec: Math.Round(avgBaselineE2E, 2),
            SpeculativeE2ETokSec: Math.Round(avgSpecE2E, 2),
            TotalDraftTokensSpeculated: totalSpeculated,
            TotalDraftTokensAccepted: totalAccepted,
            TotalVerificationSteps: totalSteps,
            MeanAcceptedTokensPerStep: Math.Round(mu, 2),
            TotalRejections: totalRejections
        );

        return new ComparativeBenchmarkResult(
            DateTime.UtcNow,
            config,
            summary,
            workloadResults
        );
    }
}
