using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;

namespace Klydis.Core.Protocol;

/// <summary>
/// The explicit runtime-enforced protocol for model outputs.
/// Every model output resolves into exactly one of these runtime objects.
/// Natural language reasoning alone is NEVER treated as an action.
/// </summary>
public enum AgentOutputKind
{
    ToolCall,
    Final,
    AskUser,
    Plan,
    ObservationRequest,
    Wait
}

/// <summary>
/// An authoritative structured action emitted by the model generation cycle.
/// </summary>
public sealed record AgentAction
{
    public required string ActionId { get; init; }
    public required AgentOutputKind Kind { get; init; }
    public string? ToolName { get; init; }
    public JsonDocument? Arguments { get; init; }
    public IDictionary<string, object>? ArgumentsMap { get; init; }
    public string? FinalText { get; init; }
    public string? Reason { get; init; }
    public string SourceProtocol { get; init; } = "generic";
    public bool IsHallucinatedSimulation { get; init; } = false;

    public bool IsTool(string toolName) =>
        Kind == AgentOutputKind.ToolCall &&
        string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a normalized agent decision derived deterministically from model output.
/// </summary>
public sealed record NormalizedAgentDecision
{
    public required AgentOutputKind Kind { get; init; }
    public required IReadOnlyList<AgentAction> Actions { get; init; }
    public string? Explanation { get; init; }
    public string? RawOutput { get; init; }
    public string Protocol { get; init; } = "generic";
    public bool RequiresRepair { get; init; }
    public string? RepairReason { get; init; }
}

/// <summary>
/// Detects when a model invents imaginary execution primitives or writes pseudocode/scripts
/// (e.g. from os import sysinfo, syslog(), os.info(), exec(), nvmax) instead of emitting valid tool calls.
/// </summary>
public static class HallucinatedToolDetector
{
    private static readonly Regex[] HallucinatedPatterns = new[]
    {
        // Python pseudo imports and calls
        new Regex(@"\bfrom\s+(?:os|sys|system|platform)\s+import\s+(?:sysinfo|syslog|exec|info|hwinfo|gpuinfo|nvmax)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bimport\s+(?:sysinfo|nvmax|syslog|hwinfo)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\b(?:os|sys|system)\.(?:info|sysinfo|exec|syslog|get_cpu|get_gpu|nvmax)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\b(?:sysinfo|syslog|nvmax|exec)\s*\([^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        
        // Fabricated tool syntax / execution simulation in text
        new Regex(@"(?:Input|Arguments|Parameters)\s*:\s*\{[^}]*\}\s*(?:\r?\n)+\s*(?:Output|Result|Return)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\[(?:EXECUTING|RUNNING|SIMULATING)\s+TOOL:[^\]]+\]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"<execute_tool\s+name=""[^""]+"">", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"<invoke_tool\s+name=""[^""]+"">", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    /// <summary>
    /// Checks whether the model output contains hallucinated API calls or simulated execution.
    /// </summary>
    public static (bool IsHallucinated, string? DetectedPattern, string? SuggestedTool) Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, null, null);

        foreach (var pattern in HallucinatedPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                string matched = match.Value;
                string suggested = SuggestRealTool(matched);
                return (true, matched, suggested);
            }
        }

        return (false, null, null);
    }

    private static string SuggestRealTool(string matched)
    {
        string lower = matched.ToLowerInvariant();
        if (lower.Contains("cpu") || lower.Contains("sysinfo") || lower.Contains("system") || lower.Contains("hwinfo"))
            return "system_cpu_metrics or get_system_info";
        if (lower.Contains("gpu") || lower.Contains("nvmax"))
            return "system_gpu_metrics or run_command (with nvidia-smi)";
        if (lower.Contains("exec") || lower.Contains("run"))
            return "run_command";
        if (lower.Contains("syslog") || lower.Contains("log"))
            return "run_command (with Get-EventLog / Get-WinEvent)";
        return "run_command or get_system_info";
    }
}
