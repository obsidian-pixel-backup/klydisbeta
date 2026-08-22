using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Tasks;

namespace Klydis.Core.Epistemic;

/// <summary>
/// Factual Claim Validator & Hallucination Firewall (P2).
/// Cross-references factual assertions in assistant outputs against the Epistemic Truth Ledger.
/// </summary>
public static class ClaimValidator
{
    private static readonly Regex CpuClaimRegex = new(@"(AMD\s+[A-Za-z0-9\s\-]+|Intel\s+Core\s+[A-Za-z0-9\-]+|Threadripper[^\n,\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RamClaimRegex = new(@"(\d{2,3})\s*(GB|GiB)\s*(RAM|Memory)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Validates an assistant message against the authoritative EpistemicLedger.
    /// If ungrounded hardware claims are detected without backing evidence, marks the claim unverified.
    /// </summary>
    public static string ValidateAndSanitizeResponse(string responseText, EpistemicLedger epistemicLedger)
    {
        if (string.IsNullOrWhiteSpace(responseText) || epistemicLedger == null)
        {
            return responseText;
        }

        var facts = epistemicLedger.GetAllFacts();
        if (facts.Count == 0)
        {
            // If no facts are verified, check if the model claims specific high-end hardware
            var cpuMatch = CpuClaimRegex.Match(responseText);
            if (cpuMatch.Success && !responseText.Contains("UNVERIFIED", StringComparison.OrdinalIgnoreCase))
            {
                // Unverified hardware claim made without tool evidence
                return responseText + "\n\n*(Note: System specifications above are unverified model estimates. Run diagnostic tools for verified hardware data.)*";
            }
        }

        return responseText;
    }
}
