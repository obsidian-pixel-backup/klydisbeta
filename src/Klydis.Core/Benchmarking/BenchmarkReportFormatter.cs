using System;
using System.Text;
using System.Text.Json;

namespace Klydis.Core.Benchmarking;

/// <summary>
/// Formats comparative benchmark results into human-readable Markdown and machine-readable JSON reports.
/// </summary>
public static class BenchmarkReportFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Exports benchmark results as a structured JSON string.
    /// </summary>
    public static string ToJson(ComparativeBenchmarkResult result, bool indented = true)
    {
        var options = indented ? JsonOptions : new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return JsonSerializer.Serialize(result, options);
    }

    /// <summary>
    /// Exports benchmark results as a formatted Markdown report.
    /// </summary>
    public static string ToMarkdown(ComparativeBenchmarkResult result)
    {
        var sb = new StringBuilder();
        var summary = result.Summary;

        sb.AppendLine("# Klydis comparative Speed & Acceptance Benchmark Report");
        sb.AppendLine();
        sb.AppendLine($"**Date**: {result.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"**Target Model**: `{result.Config.TargetModelPath}`  ");
        sb.AppendLine($"**Draft Model**: `{result.Config.DraftModelPath ?? "N/A (Draftless N-gram Fallback)"}`  ");
        sb.AppendLine($"**Mock Execution**: `{result.Config.IsMockExecution}`  ");
        sb.AppendLine();

        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Baseline Inference | Speculative Decoding | Speedup / Change |");
        sb.AppendLine("|---|---|---|---|");

        double genChangePercent = summary.BaselineGenTokSec > 0 
            ? Math.Round(((summary.SpeculativeGenTokSec - summary.BaselineGenTokSec) / summary.BaselineGenTokSec) * 100.0, 1) 
            : 0;
        string genChangeSign = genChangePercent >= 0 ? "+" : "";

        double ttftDiff = Math.Round(summary.SpeculativeTtftMs - summary.BaselineTtftMs, 1);
        double ttftPercent = summary.BaselineTtftMs > 0 ? Math.Round((ttftDiff / summary.BaselineTtftMs) * 100.0, 1) : 0;
        string ttftSign = ttftDiff >= 0 ? "+" : "";

        double e2eChangePercent = summary.BaselineE2ETokSec > 0
            ? Math.Round(((summary.SpeculativeE2ETokSec - summary.BaselineE2ETokSec) / summary.BaselineE2ETokSec) * 100.0, 1)
            : 0;
        string e2eChangeSign = e2eChangePercent >= 0 ? "+" : "";

        sb.AppendLine($"| **Generation Speed (tok/s)** | {summary.BaselineGenTokSec:F2} tok/s | {summary.SpeculativeGenTokSec:F2} tok/s | **{summary.SpeedupRatio:F2}x Speedup** ({genChangeSign}{genChangePercent}%) |");
        sb.AppendLine($"| **Time-To-First-Token (TTFT)** | {summary.BaselineTtftMs:F2} ms | {summary.SpeculativeTtftMs:F2} ms | {ttftSign}{ttftDiff:F1} ms ({ttftSign}{ttftPercent}%) |");
        sb.AppendLine($"| **Draft Acceptance Rate (α)** | N/A | **{summary.DraftAcceptanceRate * 100.0:F2}%** | Accepted: {summary.TotalDraftTokensAccepted} / {summary.TotalDraftTokensSpeculated} tokens |");
        sb.AppendLine($"| **End-to-End Throughput** | {summary.BaselineE2ETokSec:F2} tok/s | {summary.SpeculativeE2ETokSec:F2} tok/s | **{summary.SpeedupRatio:F2}x Speedup** ({e2eChangeSign}{e2eChangePercent}%) |");
        sb.AppendLine();

        sb.AppendLine("## Detailed Workload Performance Breakdown");
        sb.AppendLine();

        int workloadIndex = 1;
        foreach (var workload in result.WorkloadResults)
        {
            sb.AppendLine($"### {workloadIndex}. {workload.Profile.Name} ({workload.Profile.WorkloadType})");
            sb.AppendLine($"- **Baseline**: Generation: `{workload.BaselineGenTokSec.Mean:F2} tok/s` | TTFT: `{workload.BaselineTtftMs.Mean:F2} ms` | E2E: `{workload.BaselineE2ETokSec.Mean:F2} tok/s`");
            
            if (workload.SpeculativeGenTokSec != null)
            {
                double alpha = workload.SpeculativeTelemetry?.DraftAcceptanceRate ?? 0;
                sb.AppendLine($"- **Speculative**: Generation: `{workload.SpeculativeGenTokSec.Mean:F2} tok/s` | TTFT: `{workload.SpeculativeTtftMs?.Mean:F2} ms` | α: `{alpha * 100.0:F1}%`");
                sb.AppendLine($"- **Workload Speedup Ratio (S)**: **{workload.SpeedupRatio:F2}x**");
            }
            else
            {
                sb.AppendLine("- **Speculative**: N/A");
            }

            sb.AppendLine();
            workloadIndex++;
        }

        sb.AppendLine("## Acceptance Telemetry Breakdown");
        sb.AppendLine();
        sb.AppendLine($"- **Total Speculated Draft Tokens**: {summary.TotalDraftTokensSpeculated:N0}");
        sb.AppendLine($"- **Total Accepted Draft Tokens**: {summary.TotalDraftTokensAccepted:N0}");
        sb.AppendLine($"- **Total Verification Steps**: {summary.TotalVerificationSteps:N0}");
        sb.AppendLine($"- **Mean Accepted Tokens per Verification Step (μ)**: `{summary.MeanAcceptedTokensPerStep:F2}`");
        sb.AppendLine($"- **Total Candidate Rejections**: {summary.TotalRejections:N0}");
        sb.AppendLine();

        return sb.ToString();
    }
}
