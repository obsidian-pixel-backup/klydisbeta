using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Tasks;

namespace Klydis.Core.Epistemic;

/// <summary>
/// Result of evaluating a shell command's epistemic provenance.
/// </summary>
public sealed record CommandEpistemicAnalysis(
    bool IsPureModelClaim,
    EpistemicSource Source,
    EpistemicAuthority Authority,
    string Reason);

/// <summary>
/// Diagnostic Evidence Guard (P0).
/// Prevents hallucinated or model-authored strings (e.g. `Write-Output "CPU: 42%"`)
/// from being mistaken for real execution evidence. Validates semantic ranges,
/// cross-fact mathematical consistency, and temporal freshness.
/// </summary>
public static class DiagnosticEvidenceGuard
{
    private static readonly Regex PureEchoRegex = new(
        @"^\s*(write-output|echo|printf|print|console\.log)\s+(""[^""]*""|'[^']*'|`[^`]*`)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] PureEchoVerbs =
    {
        "write-output", "echo", "printf", "print", "console.log"
    };

    private static readonly string[] ExternalObservationSignals =
    {
        "get-ciminstance", "get-wmiobject", "get-process", "get-counter", "wmic",
        "nvidia-smi", "get-volume", "get-disk", "get-physicaldisk", "get-netadapter",
        "get-service", "systeminfo", "tasklist", "netstat", "ipconfig",
        "/proc/", "ps -", "top ", "free -", "vmstat", "iostat", "uname"
    };

    /// <summary>
    /// Analyzes a command string to determine whether it actually queries external state
    /// or merely echoes model-supplied literal answers.
    /// </summary>
    public static CommandEpistemicAnalysis AnalyzeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CommandEpistemicAnalysis(
                IsPureModelClaim: true,
                Source: EpistemicSource.ModelClaim,
                Authority: EpistemicAuthority.Untrusted,
                Reason: "Empty or null command.");
        }

        string cmdTrim = command.Trim();
        string cmdLower = cmdTrim.ToLowerInvariant();

        // 1. Check for pure echo/Write-Output string literals
        if (PureEchoRegex.IsMatch(cmdTrim))
        {
            return new CommandEpistemicAnalysis(
                IsPureModelClaim: true,
                Source: EpistemicSource.ModelClaim,
                Authority: EpistemicAuthority.Untrusted,
                Reason: "Command consists solely of a string literal echo (model-authored value).");
        }

        // 2. Check multi-line Write-Output / echo sequences without external commands
        var lines = cmdTrim.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Trim())
                           .Where(l => !string.IsNullOrEmpty(l))
                           .ToList();

        if (lines.Count > 0 && lines.All(l =>
        {
            string lowerLine = l.ToLowerInvariant();
            return PureEchoVerbs.Any(v => lowerLine.StartsWith(v, StringComparison.OrdinalIgnoreCase)) &&
                   !ExternalObservationSignals.Any(s => lowerLine.Contains(s, StringComparison.OrdinalIgnoreCase));
        }))
        {
            return new CommandEpistemicAnalysis(
                IsPureModelClaim: true,
                Source: EpistemicSource.ModelClaim,
                Authority: EpistemicAuthority.Untrusted,
                Reason: "Command contains only print/echo statements without querying system APIs.");
        }

        // 3. Check for genuine external observation queries
        if (ExternalObservationSignals.Any(s => cmdLower.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return new CommandEpistemicAnalysis(
                IsPureModelClaim: false,
                Source: EpistemicSource.RuntimeTool,
                Authority: EpistemicAuthority.Observed,
                Reason: "Command queries authoritative operating system APIs / counters.");
        }

        // 4. General command execution (default fallback)
        return new CommandEpistemicAnalysis(
            IsPureModelClaim: false,
            Source: EpistemicSource.RuntimeTool,
            Authority: EpistemicAuthority.Derived,
            Reason: "Generic shell command execution.");
    }

    /// <summary>
    /// Validates that a numeric observation falls within plausible physical/operational bounds.
    /// </summary>
    public static (bool IsValid, string? Error) ValidateNumericMetric(string metricName, double value)
    {
        string name = metricName.ToLowerInvariant();

        if (name.Contains("percent") || name.Contains("usage") || name.Contains("utilization") || name.Contains("load"))
        {
            if (value < 0.0 || value > 100.0)
            {
                return (false, $"Metric '{metricName}' value {value} is out of valid percentage range [0, 100].");
            }
        }
        else if (name.Contains("temp") || name.Contains("temperature"))
        {
            if (value < -50.0 || value > 150.0)
            {
                return (false, $"Temperature '{metricName}' value {value}°C is outside plausible range [-50, 150].");
            }
        }
        else if (name.Contains("core") || name.Contains("process_count") || name.Contains("thread_count"))
        {
            if (value < 0.0)
            {
                return (false, $"Count '{metricName}' value {value} must be non-negative.");
            }
        }
        else if (name.Contains("uptime"))
        {
            if (value < 0.0)
            {
                return (false, $"Uptime '{metricName}' value {value} must be non-negative.");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Validates cross-metric mathematical consistency for memory usage.
    /// </summary>
    public static (bool IsConsistent, string? Error) ValidateMemoryConsistency(double usedGb, double availableGb, double totalGb)
    {
        if (totalGb <= 0.0)
        {
            return (false, "Total RAM must be greater than zero.");
        }

        if (usedGb > totalGb + 0.5)
        {
            return (false, $"Used RAM ({usedGb:F1} GB) exceeds total RAM ({totalGb:F1} GB).");
        }

        if (availableGb > totalGb + 0.5)
        {
            return (false, $"Available RAM ({availableGb:F1} GB) exceeds total RAM ({totalGb:F1} GB).");
        }

        double sum = usedGb + availableGb;
        if (sum > totalGb * 1.25 || sum < totalGb * 0.75)
        {
            return (false, $"Memory sum inconsistent: Used ({usedGb:F1} GB) + Available ({availableGb:F1} GB) != Total ({totalGb:F1} GB).");
        }

        return (true, null);
    }

    /// <summary>
    /// Validates cross-metric mathematical consistency for disk storage.
    /// </summary>
    public static (bool IsConsistent, string? Error) ValidateDiskConsistency(double usedGb, double freeGb, double totalGb)
    {
        if (totalGb <= 0.0)
        {
            return (false, "Total disk size must be greater than zero.");
        }

        if (freeGb > totalGb + 0.5)
        {
            return (false, $"Free disk space ({freeGb:F1} GB) exceeds total capacity ({totalGb:F1} GB).");
        }

        double sum = usedGb + freeGb;
        if (Math.Abs(sum - totalGb) > totalGb * 0.15 + 1.0)
        {
            return (false, $"Storage sum inconsistent: Used ({usedGb:F1} GB) + Free ({freeGb:F1} GB) != Total ({totalGb:F1} GB).");
        }

        return (true, null);
    }

    /// <summary>
    /// Gets the maximum acceptable age for an authoritative fact based on property kind.
    /// </summary>
    public static TimeSpan GetFreshnessTtl(string domain, string property)
    {
        string prop = property.ToLowerInvariant();
        string dom = domain.ToLowerInvariant();

        if (prop.Contains("usage") || prop.Contains("util") || prop.Contains("load") || prop.Contains("temp") || prop.Contains("free_vram"))
        {
            return TimeSpan.FromSeconds(5);
        }

        if (dom == "process" || prop.Contains("available_gb") || prop.Contains("memory") || prop.Contains("processes"))
        {
            return TimeSpan.FromSeconds(30);
        }

        if (prop.Contains("disk") || prop.Contains("drives") || prop.Contains("display") || prop.Contains("network"))
        {
            return TimeSpan.FromMinutes(10);
        }

        // Static hardware / OS specifications
        return TimeSpan.FromHours(24);
    }
}
