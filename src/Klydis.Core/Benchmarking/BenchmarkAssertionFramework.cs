using System;

namespace Klydis.Core.Benchmarking;

/// <summary>
/// Exception thrown when benchmark performance criteria or telemetry thresholds fail assertion.
/// </summary>
public class BenchmarkAssertionException : Exception
{
    public BenchmarkAssertionException(string message) : base(message) { }
}

/// <summary>
/// Automated assertion framework for validating comparative benchmark performance criteria in CI/CD workflows.
/// </summary>
public static class BenchmarkAssertionFramework
{
    /// <summary>
    /// Asserts that speculative decoding achieves minimum required speedup without latency regression or unacceptable acceptance rates.
    /// </summary>
    /// <param name="result">The comparative benchmark result to evaluate.</param>
    /// <param name="minSpeedupRatio">Minimum required speculative speedup ratio S (default: 1.15x / +15%).</param>
    /// <param name="minDraftAcceptanceRate">Minimum required draft acceptance rate alpha (default: 0.50 / 50%).</param>
    /// <param name="maxTtftRegressionRatio">Maximum allowable Time-To-First-Token regression ratio (default: 1.35x / +35%).</param>
    public static void AssertPerformanceCriteria(
        ComparativeBenchmarkResult result,
        double minSpeedupRatio = 1.15,
        double minDraftAcceptanceRate = 0.50,
        double maxTtftRegressionRatio = 1.35)
    {
        ArgumentNullException.ThrowIfNull(result);
        var summary = result.Summary;

        if (summary.SpeedupRatio < minSpeedupRatio)
        {
            throw new BenchmarkAssertionException(
                $"[PERFORMANCE FAILURE] Speculative speedup ratio S={summary.SpeedupRatio:F2}x is below required minimum threshold S_min={minSpeedupRatio:F2}x. " +
                $"(Baseline: {summary.BaselineGenTokSec:F1} tok/s vs Speculative: {summary.SpeculativeGenTokSec:F1} tok/s)");
        }

        if (summary.DraftAcceptanceRate < minDraftAcceptanceRate)
        {
            throw new BenchmarkAssertionException(
                $"[ACCEPTANCE FAILURE] Draft acceptance rate α={summary.DraftAcceptanceRate * 100.0:F1}% is below required threshold α_min={minDraftAcceptanceRate * 100.0:F1}%.");
        }

        double ttftRatio = summary.BaselineTtftMs > 0 ? summary.SpeculativeTtftMs / summary.BaselineTtftMs : 1.0;
        if (ttftRatio > maxTtftRegressionRatio)
        {
            throw new BenchmarkAssertionException(
                $"[LATENCY FAILURE] Time-To-First-Token regression TTFT_ratio={ttftRatio:F2}x exceeds maximum allowable regression {maxTtftRegressionRatio:F2}x. " +
                $"(Baseline TTFT: {summary.BaselineTtftMs:F1}ms vs Speculative TTFT: {summary.SpeculativeTtftMs:F1}ms)");
        }
    }
}
