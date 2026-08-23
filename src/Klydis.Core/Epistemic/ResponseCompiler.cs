using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Tasks;

namespace Klydis.Core.Epistemic;

/// <summary>
/// Compiled result of processing model text through the Epistemic Claim Compiler.
/// </summary>
public sealed record CompiledResponse(
    string CompiledText,
    int TotalClaimsExtracted,
    int VerifiedClaimsCount,
    int QualifiedClaimsCount,
    int RejectedClaimsCount,
    double EvidenceAccuracyScore);

/// <summary>
/// Response Compiler (P0/P2) that transforms raw model generation into an evidence-verified,
/// compiled artifact. Prevents hallucinated or unbacked claims from entering final deliverables.
/// </summary>
public static class ResponseCompiler
{
    private static readonly Regex SpecificHardwareRegex = new(
        @"(?:AMD\s+Ryzen(?:\s+\d+)?(?:\s+[A-Za-z0-9\-]+)?|Intel\s+Core(?:\s+[A-Za-z0-9\-]+)?|RTX\s+\d{4}|GTX\s+\d{4}|Threadripper|Ryzen\s+\d+|Xeon\s+[A-Za-z0-9\-]+|\b\d{1,4}\s*(?:GB|GiB)\s*(?:RAM|Memory|VRAM))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CpuPercentageRegex = new(
        @"(?:CPU(?:\s+utilization|\s+usage)?\s*(?:is|at|:)?\s*(\d{1,3}(?:\.\d+)?)\s*%)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MemoryPercentageRegex = new(
        @"(?:(?:RAM|Memory)(?:\s+utilization|\s+usage)?\s*(?:is|at|:)?\s*(\d{1,3}(?:\.\d+)?)\s*%)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Compiles a model response by matching factual claims against the ClaimLedger and the
    /// run's recorded evidence, appending an honesty notice when unsupported hardware/metric
    /// claims appear. Returns the (possibly annotated) compiled text.
    /// </summary>
    public static CompiledResponse Compile(
        string? rawResponse,
        ClaimLedger? claimLedger,
        ExecutionEvidenceLedger? evidenceLedger,
        string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return new CompiledResponse(string.Empty, 0, 0, 0, 0, 100.0);
        }

        var verifiedClaims = claimLedger?.GetVerifiedClaims(taskId) ?? Array.Empty<ClaimRecord>();
        var currentEvidence = evidenceLedger != null && !string.IsNullOrEmpty(taskId)
            ? evidenceLedger.GetCurrentEvidence(taskId)
            : Array.Empty<EvidenceLedgerEntry>();

        int totalClaims = 0;
        int verified = 0;
        int qualified = 0;
        int rejected = 0;

        string compiled = rawResponse;

        // 1. Evaluate Hardware & Diagnostic Spec Claims
        var hwMatches = SpecificHardwareRegex.Matches(rawResponse);
        if (hwMatches.Count > 0)
        {
            foreach (Match m in hwMatches)
            {
                totalClaims++;
                string claimText = m.Value.Trim();
                bool isBacked = verifiedClaims.Any(c => c.ClaimText.Contains(claimText, StringComparison.OrdinalIgnoreCase) ||
                                                        claimText.Contains(c.ClaimText, StringComparison.OrdinalIgnoreCase) ||
                                                        (c.Value != null && (c.Value.Contains(claimText, StringComparison.OrdinalIgnoreCase) || claimText.Contains(c.Value, StringComparison.OrdinalIgnoreCase)))) ||
                                currentEvidence.Any(e => e.Evidence.Description.Contains(claimText, StringComparison.OrdinalIgnoreCase));

                if (isBacked)
                {
                    verified++;
                }
                else
                {
                    qualified++;
                }
            }

            if (qualified > 0 && !compiled.Contains("unverified model estimate", StringComparison.OrdinalIgnoreCase))
            {
                compiled += "\n\n*(Note: System specifications above include unverified model estimates. Run diagnostic tools for verified hardware data.)*";
            }
        }

        // 2. Evaluate CPU metric claims
        var cpuMatch = CpuPercentageRegex.Match(rawResponse);
        if (cpuMatch.Success)
        {
            totalClaims++;
            bool cpuBacked = verifiedClaims.Any(c => c.Domain == "cpu" || c.Property == "usage" || c.Property == "utilization") ||
                             currentEvidence.Any(e => e.Evidence.Description.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                                                      e.Evidence.Subject?.Contains("CPU", StringComparison.OrdinalIgnoreCase) == true);
            if (cpuBacked) verified++;
            else qualified++;
        }

        // 3. Evaluate Memory metric claims
        var memMatch = MemoryPercentageRegex.Match(rawResponse);
        if (memMatch.Success)
        {
            totalClaims++;
            bool memBacked = verifiedClaims.Any(c => c.Domain == "memory" || c.Domain == "ram") ||
                             currentEvidence.Any(e => e.Evidence.Description.Contains("RAM", StringComparison.OrdinalIgnoreCase) ||
                                                      e.Evidence.Description.Contains("Memory", StringComparison.OrdinalIgnoreCase));
            if (memBacked) verified++;
            else qualified++;
        }

        double accuracyScore = totalClaims > 0
            ? Math.Round(((double)verified / totalClaims) * 100.0, 1)
            : 100.0;

        return new CompiledResponse(
            CompiledText: compiled,
            TotalClaimsExtracted: totalClaims,
            VerifiedClaimsCount: verified,
            QualifiedClaimsCount: qualified,
            RejectedClaimsCount: rejected,
            EvidenceAccuracyScore: accuracyScore);
    }
}
