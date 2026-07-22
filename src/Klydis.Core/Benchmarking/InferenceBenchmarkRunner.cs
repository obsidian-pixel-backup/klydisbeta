using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Chat;
using Klydis.Core.Inference.Telemetry;

namespace Klydis.Core.Benchmarking;

/// <summary>
/// Executes comparative benchmark suites measuring throughput (tok/s), Time-To-First-Token (TTFT),
/// and draft acceptance rate (alpha) across baseline and speculative inference.
/// </summary>
public class InferenceBenchmarkRunner
{
    private readonly IInferenceEngine _engine;
    private readonly ILogger<InferenceBenchmarkRunner>? _logger;

    public InferenceBenchmarkRunner(IInferenceEngine engine, ILogger<InferenceBenchmarkRunner>? logger = null)
    {
        _engine = engine;
        _logger = logger;
    }

    /// <summary>
    /// Executes a comparative benchmark suite across all configured prompt profiles.
    /// </summary>
    public async Task<ComparativeBenchmarkResult> RunComparativeBenchmarkAsync(
        BenchmarkConfig config,
        CancellationToken ct = default)
    {
        config ??= new BenchmarkConfig();
        _logger?.LogInformation("Starting Comparative Benchmark for Target: {TargetModel}", config.TargetModelPath);

        if (config.IsMockExecution)
        {
            return RunMockBenchmark(config);
        }

        var workloadResults = new List<WorkloadBenchmarkResult>();

        foreach (var profile in config.EffectivePrompts)
        {
            ct.ThrowIfCancellationRequested();
            _logger?.LogInformation("Running Benchmark Workload: {WorkloadName}", profile.Name);

            // 1. Run Baseline (Single-Model Inference)
            _engine.IsSpeculativeDecodingEnabled = false;
            var baselineTelemetry = await RunPromptIterationsAsync(profile, config, ct);
            var baselineGenDist = MetricDistribution.FromValues(baselineTelemetry.Select(t => t.GenerationTokensPerSecond));
            var baselineTtftDist = MetricDistribution.FromValues(baselineTelemetry.Select(t => t.TimeToFirstTokenMs));
            var baselineE2EDist = MetricDistribution.FromValues(baselineTelemetry.Select(t => t.EndToEndTokensPerSecond));

            // 2. Run Speculative Decoding
            List<InferenceTelemetry> specTelemetry = new();
            MetricDistribution? specGenDist = null;
            MetricDistribution? specTtftDist = null;
            MetricDistribution? specE2EDist = null;
            SpeculativeTelemetry? aggregatedSpecTelemetry = null;

            if (_engine.SpeculativeEngine.IsLoaded || !string.IsNullOrEmpty(config.DraftModelPath))
            {
                _engine.IsSpeculativeDecodingEnabled = true;
                _engine.SpeculativeDraftCount = config.SpeculativeDraftCount;

                if (!_engine.SpeculativeEngine.IsLoaded && !string.IsNullOrEmpty(config.DraftModelPath) && _engine.CurrentModelPath != null)
                {
                    await _engine.AttachSpeculativeDraftAsync(_engine.CurrentModelPath);
                }

                specTelemetry = await RunPromptIterationsAsync(profile, config, ct);
                specGenDist = MetricDistribution.FromValues(specTelemetry.Select(t => t.GenerationTokensPerSecond));
                specTtftDist = MetricDistribution.FromValues(specTelemetry.Select(t => t.TimeToFirstTokenMs));
                specE2EDist = MetricDistribution.FromValues(specTelemetry.Select(t => t.EndToEndTokensPerSecond));

                // Aggregate SpeculativeTelemetry across iterations
                int specTokens = specTelemetry.Sum(t => t.SpeculativeMetrics?.TotalDraftTokensSpeculated ?? 0);
                int acceptedTokens = specTelemetry.Sum(t => t.SpeculativeMetrics?.TotalDraftTokensAccepted ?? 0);
                int steps = specTelemetry.Sum(t => t.SpeculativeMetrics?.TotalVerificationSteps ?? 0);
                int rejections = specTelemetry.Sum(t => t.SpeculativeMetrics?.TotalRejections ?? 0);
                int fallbacks = specTelemetry.Sum(t => t.SpeculativeMetrics?.TotalFallbackSteps ?? 0);

                aggregatedSpecTelemetry = SpeculativeTelemetry.Calculate(specTokens, acceptedTokens, steps, rejections, fallbacks);
            }

            double speedup = (baselineGenDist.Mean > 0 && specGenDist != null)
                ? Math.Round(specGenDist.Mean / baselineGenDist.Mean, 2)
                : 1.0;

            workloadResults.Add(new WorkloadBenchmarkResult(
                profile,
                baselineGenDist,
                baselineTtftDist,
                baselineE2EDist,
                specGenDist,
                specTtftDist,
                specE2EDist,
                aggregatedSpecTelemetry,
                speedup,
                baselineTelemetry,
                specTelemetry
            ));
        }

        return ComparativeBenchmarkResult.Aggregate(config, workloadResults);
    }

    private async Task<List<InferenceTelemetry>> RunPromptIterationsAsync(
        BenchmarkPromptProfile profile,
        BenchmarkConfig config,
        CancellationToken ct)
    {
        var inferenceParams = new LLama.Common.InferenceParams
        {
            MaxTokens = profile.MaxTokensToGenerate,
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
            {
                Temperature = profile.Temperature,
                TopP = profile.TopP
            }
        };

        // Warmup iterations
        for (int w = 0; w < config.WarmupIterations; w++)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var _ in _engine.GenerateAsync(profile.PromptText, inferenceParams, triggerEvents: false, isIsolated: true, ct: ct))
            {
            }
        }

        // Benchmark iterations
        var results = new List<InferenceTelemetry>();
        for (int i = 0; i < config.BenchmarkIterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            InferenceTelemetry? captured = null;
            Action<InferenceTelemetry> handler = t => captured = t;
            _engine.InferenceCompleted += handler;

            try
            {
                await foreach (var _ in _engine.GenerateAsync(profile.PromptText, inferenceParams, triggerEvents: false, isIsolated: true, ct: ct))
                {
                }
            }
            finally
            {
                _engine.InferenceCompleted -= handler;
            }

            captured ??= _engine.LastTelemetry;
            if (captured != null)
            {
                results.Add(captured);
            }
        }

        return results;
    }

    /// <summary>
    /// Executes a synthetic mock benchmark (fast CI path without loading real GGUF models).
    /// </summary>
    private ComparativeBenchmarkResult RunMockBenchmark(BenchmarkConfig config)
    {
        var workloadResults = new List<WorkloadBenchmarkResult>();
        var random = new Random(42);

        foreach (var profile in config.EffectivePrompts)
        {
            var baselineList = new List<InferenceTelemetry>();
            var specList = new List<InferenceTelemetry>();

            for (int i = 0; i < config.BenchmarkIterations; i++)
            {
                double baseJitter = (random.NextDouble() - 0.5) * 2.0;
                double specJitter = (random.NextDouble() - 0.5) * 4.0;
                double ttftJitter = (random.NextDouble() - 0.5) * 10.0;

                double baseGenTokSec = Math.Round(config.MockBaselineTokSec + baseJitter, 2);
                double specGenTokSec = Math.Round(config.MockSpeculativeTokSec + specJitter, 2);
                double baseTtft = Math.Round(config.MockBaselineTtftMs + ttftJitter, 2);
                double specTtft = Math.Round(config.MockSpeculativeTtftMs + ttftJitter, 2);

                int promptTokens = Math.Max(10, profile.PromptText.Length / 4);
                int genTokens = profile.MaxTokensToGenerate;

                double baseGenDurationMs = (genTokens / baseGenTokSec) * 1000.0;
                double specGenDurationMs = (genTokens / specGenTokSec) * 1000.0;

                double baseTotalMs = baseTtft + baseGenDurationMs;
                double specTotalMs = specTtft + specGenDurationMs;

                double baseE2ETokSec = Math.Round((genTokens / baseTotalMs) * 1000.0, 2);
                double specE2ETokSec = Math.Round((genTokens / specTotalMs) * 1000.0, 2);

                var baseTelemetry = new InferenceTelemetry(
                    RequestId: Guid.NewGuid().ToString("N"),
                    TargetModelPath: config.TargetModelPath,
                    DraftModelPath: null,
                    IsSpeculativeEnabled: false,
                    PromptLengthChars: profile.PromptText.Length,
                    PromptTokenCount: promptTokens,
                    GeneratedTokenCount: genTokens,
                    TimeToFirstTokenMs: baseTtft,
                    GenerationDurationMs: Math.Round(baseGenDurationMs, 2),
                    TotalElapsedMs: Math.Round(baseTotalMs, 2),
                    GenerationTokensPerSecond: baseGenTokSec,
                    EndToEndTokensPerSecond: baseE2ETokSec,
                    SpeculativeMetrics: null
                );
                baselineList.Add(baseTelemetry);

                int speculated = genTokens * 2;
                int accepted = (int)Math.Round(speculated * config.MockDraftAcceptanceRate);
                int steps = Math.Max(1, speculated / 8);
                int rejections = Math.Max(0, steps - (accepted / 4));
                var specMetrics = SpeculativeTelemetry.Calculate(speculated, accepted, steps, rejections, 0);

                var specItem = new InferenceTelemetry(
                    RequestId: Guid.NewGuid().ToString("N"),
                    TargetModelPath: config.TargetModelPath,
                    DraftModelPath: config.DraftModelPath,
                    IsSpeculativeEnabled: true,
                    PromptLengthChars: profile.PromptText.Length,
                    PromptTokenCount: promptTokens,
                    GeneratedTokenCount: genTokens,
                    TimeToFirstTokenMs: specTtft,
                    GenerationDurationMs: Math.Round(specGenDurationMs, 2),
                    TotalElapsedMs: Math.Round(specTotalMs, 2),
                    GenerationTokensPerSecond: specGenTokSec,
                    EndToEndTokensPerSecond: specE2ETokSec,
                    SpeculativeMetrics: specMetrics
                );
                specList.Add(specItem);
            }

            var baselineGenDist = MetricDistribution.FromValues(baselineList.Select(t => t.GenerationTokensPerSecond));
            var baselineTtftDist = MetricDistribution.FromValues(baselineList.Select(t => t.TimeToFirstTokenMs));
            var baselineE2EDist = MetricDistribution.FromValues(baselineList.Select(t => t.EndToEndTokensPerSecond));

            var specGenDist = MetricDistribution.FromValues(specList.Select(t => t.GenerationTokensPerSecond));
            var specTtftDist = MetricDistribution.FromValues(specList.Select(t => t.TimeToFirstTokenMs));
            var specE2EDist = MetricDistribution.FromValues(specList.Select(t => t.EndToEndTokensPerSecond));

            int totSpec = specList.Sum(t => t.SpeculativeMetrics?.TotalDraftTokensSpeculated ?? 0);
            int totAcc = specList.Sum(t => t.SpeculativeMetrics?.TotalDraftTokensAccepted ?? 0);
            int totSteps = specList.Sum(t => t.SpeculativeMetrics?.TotalVerificationSteps ?? 0);
            int totRej = specList.Sum(t => t.SpeculativeMetrics?.TotalRejections ?? 0);
            var aggSpec = SpeculativeTelemetry.Calculate(totSpec, totAcc, totSteps, totRej, 0);

            double speedup = baselineGenDist.Mean > 0 ? Math.Round(specGenDist.Mean / baselineGenDist.Mean, 2) : 1.0;

            workloadResults.Add(new WorkloadBenchmarkResult(
                profile,
                baselineGenDist,
                baselineTtftDist,
                baselineE2EDist,
                specGenDist,
                specTtftDist,
                specE2EDist,
                aggSpec,
                speedup,
                baselineList,
                specList
            ));
        }

        return ComparativeBenchmarkResult.Aggregate(config, workloadResults);
    }
}
