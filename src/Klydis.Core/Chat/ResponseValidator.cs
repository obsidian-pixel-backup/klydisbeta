using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Capabilities;
using Klydis.Core.Capabilities.Policy;

namespace Klydis.Core.Chat;

/// <summary>
/// Result of turn response validation.
/// </summary>
public sealed record ResponseValidationResult(
    bool IsValid,
    string? ViolationType,
    string? CorrectiveInstruction,
    string? DirectExecutionTool);

/// <summary>
/// Validates model output against runtime truth, capability contracts, and empirical evidence.
/// </summary>
public static class ResponseValidator
{
    private static readonly Regex ExecutionClaimRegex = new(
        @"(?:i\s+(?:have\s+)?(?:opened|launched|started|moved|resized|created|deleted|modified|installed)\s+(?:the\s+)?([a-zA-Z0-9_\-\.]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Validates an assistant response text before final delivery to the user or session history.
    /// </summary>
    public static ResponseValidationResult ValidateResponse(
        string responseText,
        IReadOnlyList<string> toolsExecutedThisTurn,
        IEnumerable<string>? exposedToolNames = null,
        ICapabilityRegistry? capabilityRegistry = null)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new ResponseValidationResult(true, null, null, null);
        }

        // 1. Contradiction Evaluation: Refusal when capabilities exist
        var contradiction = CapabilityContradictionDetector.Evaluate(responseText, exposedToolNames, capabilityRegistry);
        if (contradiction.HasContradiction)
        {
            string instruction = $"RUNTIME ERROR: You claimed inability to access/inspect '{contradiction.ViolatedCapability}', but you have active access via tool '{contradiction.RecommendedToolName}'. Execute '{contradiction.RecommendedToolName}' immediately.";
            return new ResponseValidationResult(
                IsValid: false,
                ViolationType: "CapabilityContradiction",
                CorrectiveInstruction: instruction,
                DirectExecutionTool: contradiction.RecommendedToolName);
        }

        // 2. Hallucinated Execution or Pseudocode Inventions
        var (isHallucinated, detectedPattern, suggestedTool) = Protocol.HallucinatedToolDetector.Detect(responseText);
        if (isHallucinated)
        {
            string instruction = $"[TOOL_CALL_REJECTED]\n" +
                                 $"Reason: UNKNOWN_TOOL / SIMULATION_DETECTED\n" +
                                 $"You generated pseudo-code or an imaginary API invocation: '{detectedPattern}'.\n" +
                                 $"The runtime does not permit simulating execution in text or writing code against nonexistent libraries.\n" +
                                 $"Available alternatives: {suggestedTool ?? "execute real registered tools such as run_command, system_cpu_metrics, system_gpu_metrics, get_system_info"}.\n" +
                                 $"Emit a valid structured <tool_call> block immediately.";
            return new ResponseValidationResult(
                IsValid: false,
                ViolationType: "HallucinatedExecutionSimulation",
                CorrectiveInstruction: instruction,
                DirectExecutionTool: suggestedTool);
        }

        // 3. Execution Claim Without Evidence
        if (toolsExecutedThisTurn.Count == 0)
        {
            var match = ExecutionClaimRegex.Match(responseText);
            if (match.Success)
            {
                string target = match.Groups[1].Value;
                if (!string.Equals(target, "a", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(target, "the", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(target, "this", StringComparison.OrdinalIgnoreCase))
                {
                    // Model claimed it launched/opened/created something, but zero tools were called
                    string instruction = $"RUNTIME NOTICE: You stated you performed an action on '{target}', but no execution tool was called. Call the appropriate tool to execute the action.";
                    return new ResponseValidationResult(
                        IsValid: false,
                        ViolationType: "ExecutionClaimWithoutEvidence",
                        CorrectiveInstruction: instruction,
                        DirectExecutionTool: null);
                }
            }
        }

        return new ResponseValidationResult(true, null, null, null);
    }
}
