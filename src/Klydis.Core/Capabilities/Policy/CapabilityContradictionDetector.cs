using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;

namespace Klydis.Core.Capabilities.Policy;

/// <summary>
/// Result of contradiction evaluation between model statements and runtime capability truth.
/// </summary>
public sealed record CapabilityContradictionResult(
    bool HasContradiction,
    string? ViolatedCapability,
    string? RecommendedToolName,
    string? Explanation);

/// <summary>
/// Detects when model text output contradicts actual available runtime capabilities
/// (e.g. model claiming "I cannot inspect your GPU" when system_gpu_metrics is available).
/// </summary>
public static class CapabilityContradictionDetector
{
    private static readonly Regex RefusalRegex = new(
        @"(?:i\s+(?:do\s+not|don't|cannot|can't)\s+(?:have\s+)?(?:access|inspect|check|see|view|retrieve|read)|as\s+an\s+ai\b|i\s+am\s+an\s+ai\b|i\s+am\s+unable\s+to\s+(?:access|inspect|check)|do\s+not\s+have\s+(?:direct\s+)?access\s+to\s+(?:your|the)\s+(?:machine|hardware|system|local|computer|pc|gpu|cpu|ram|os))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Evaluates model response text against exposed tools and capability registry.
    /// </summary>
    public static CapabilityContradictionResult Evaluate(
        string responseText,
        IEnumerable<string>? exposedToolNames = null,
        ICapabilityRegistry? capabilityRegistry = null)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new CapabilityContradictionResult(false, null, null, null);
        }

        var toolSet = new HashSet<string>(exposedToolNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        bool hasRefusal = RefusalRegex.IsMatch(responseText);
        if (!hasRefusal)
        {
            return new CapabilityContradictionResult(false, null, null, null);
        }

        string lower = responseText.ToLowerInvariant();

        // 1. GPU denial
        if (lower.Contains("gpu") || lower.Contains("graphics") || lower.Contains("vram"))
        {
            if (toolSet.Contains("system_gpu_metrics") || capabilityRegistry?.Contains("hardware.gpu.inspect") == true)
            {
                return new CapabilityContradictionResult(
                    HasContradiction: true,
                    ViolatedCapability: "system_gpu_metrics",
                    RecommendedToolName: "system_gpu_metrics",
                    Explanation: "Model claimed lack of GPU access, but system_gpu_metrics is available.");
            }
        }

        // 2. CPU denial
        if (lower.Contains("cpu") || lower.Contains("processor"))
        {
            if (toolSet.Contains("system_cpu_metrics") || capabilityRegistry?.Contains("hardware.cpu.inspect") == true)
            {
                return new CapabilityContradictionResult(
                    HasContradiction: true,
                    ViolatedCapability: "system_cpu_metrics",
                    RecommendedToolName: "system_cpu_metrics",
                    Explanation: "Model claimed lack of CPU access, but system_cpu_metrics is available.");
            }
        }

        // 3. Memory / RAM denial
        if (lower.Contains("ram") || lower.Contains("memory"))
        {
            if (toolSet.Contains("system_memory_metrics") || capabilityRegistry?.Contains("hardware.ram.inspect") == true)
            {
                return new CapabilityContradictionResult(
                    HasContradiction: true,
                    ViolatedCapability: "system_memory_metrics",
                    RecommendedToolName: "system_memory_metrics",
                    Explanation: "Model claimed lack of Memory access, but system_memory_metrics is available.");
            }
        }

        // 4. OS info denial
        if (lower.Contains("os") || lower.Contains("operating system") || lower.Contains("windows"))
        {
            if (toolSet.Contains("system_os_info") || capabilityRegistry?.Contains("os.info") == true)
            {
                return new CapabilityContradictionResult(
                    HasContradiction: true,
                    ViolatedCapability: "system_os_info",
                    RecommendedToolName: "system_os_info",
                    Explanation: "Model claimed lack of OS information access, but system_os_info is available.");
            }
        }

        // 5. Running processes denial
        if (lower.Contains("process") || lower.Contains("processes") || lower.Contains("tasks"))
        {
            if (toolSet.Contains("system_processes") || capabilityRegistry?.Contains("os.processes.enumerate") == true)
            {
                return new CapabilityContradictionResult(
                    HasContradiction: true,
                    ViolatedCapability: "system_processes",
                    RecommendedToolName: "system_processes",
                    Explanation: "Model claimed lack of process enumeration access, but system_processes is available.");
            }
        }

        // 6. Generic machine access denial
        if (lower.Contains("machine") || lower.Contains("hardware") || lower.Contains("computer") || lower.Contains("system"))
        {
            if (toolSet.Contains("system_report") || toolSet.Contains("run_command"))
            {
                return new CapabilityContradictionResult(
                    HasContradiction: true,
                    ViolatedCapability: "system_report",
                    RecommendedToolName: "system_report",
                    Explanation: "Model claimed lack of machine access, but runtime execution tools are available.");
            }
        }

        return new CapabilityContradictionResult(false, null, null, null);
    }
}
