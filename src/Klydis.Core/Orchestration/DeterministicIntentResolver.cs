using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Klydis.Core.Orchestration;

/// <summary>
/// Result of deterministic intent resolution with confidence and extracted parameters.
/// </summary>
public sealed record DeterministicIntentResult(
    DirectActionRoute? Route,
    double Confidence,
    string NormalizedQuery,
    string? DetectedIntent);

/// <summary>
/// Multi-signal deterministic intent and entity resolver that maps user operational requests
/// to direct runtime capabilities with high confidence and zero LLM latency.
/// </summary>
public static class DeterministicIntentResolver
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes query text by standardizing contractions, stripping non-alphanumeric noise,
    /// and removing conversational filler prefixes.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string text = input.Trim().ToLowerInvariant();

        // Normalize common contractions
        text = text.Replace("what's", "what is")
                   .Replace("how's", "how is")
                   .Replace("there's", "there is")
                   .Replace("can't", "cannot")
                   .Replace("don't", "do not")
                   .Replace("won't", "will not")
                   .Replace("i'd", "i would")
                   .Replace("it's", "it is");

        // Strip conversational filler prefixes
        string[] fillerPrefixes =
        {
            "can you please ", "could you please ", "please ", "can you ", "could you ",
            "tell me ", "show me ", "give me ", "get me ", "let me know ", "i want to know ",
            "i would like to know ", "check ", "inspect ", "find out ", "confirm "
        };

        foreach (var prefix in fillerPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(prefix.Length).TrimStart();
                break;
            }
        }

        // Clean trailing punctuation
        text = text.TrimEnd('?', '!', '.', ',', ';', ':', '\r', '\n');
        text = WhitespaceRegex.Replace(text, " ").Trim();

        return text;
    }

    /// <summary>
    /// Evaluates user message against multi-signal entity and intent matchers.
    /// </summary>
    public static DeterministicIntentResult Resolve(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new DeterministicIntentResult(null, 0.0, string.Empty, null);
        }

        string rawClean = message.Trim().TrimEnd('.', '?', '!', '\r', '\n');
        string normalized = Normalize(message);

        // 1. App Launch Intent
        var appRoute = TryResolveAppLaunch(rawClean, normalized);
        if (appRoute != null)
        {
            return new DeterministicIntentResult(appRoute, 1.0, normalized, "AppLaunch");
        }

        // 2. Full System Report / Diagnostics
        if (IsSystemReportIntent(normalized))
        {
            var route = new DirectActionRoute(
                DirectActionKind.SystemReport,
                "system_report",
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                "Full system diagnostic report");
            return new DeterministicIntentResult(route, 1.0, normalized, "SystemReport");
        }

        // 3. GPU / Graphics / VRAM Metrics (Check before RAM so 'vram' is routed to GPU)
        if (IsGpuIntent(normalized))
        {
            var route = new DirectActionRoute(
                DirectActionKind.GpuMetrics,
                "system_gpu_metrics",
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                "GPU utilization and telemetry");
            return new DeterministicIntentResult(route, 1.0, normalized, "GpuMetrics");
        }

        // 4. CPU / Processor Metrics
        if (IsCpuIntent(normalized))
        {
            var route = new DirectActionRoute(
                DirectActionKind.CpuMetrics,
                "system_cpu_metrics",
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                "CPU utilization metrics");
            return new DeterministicIntentResult(route, 1.0, normalized, "CpuMetrics");
        }

        // 5. Memory / RAM Metrics
        if (IsMemoryIntent(normalized))
        {
            var route = new DirectActionRoute(
                DirectActionKind.MemoryMetrics,
                "system_memory_metrics",
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                "Memory and RAM utilization");
            return new DeterministicIntentResult(route, 1.0, normalized, "MemoryMetrics");
        }

        // 6. Disk / Storage Metrics
        if (IsDiskIntent(normalized))
        {
            var route = new DirectActionRoute(
                DirectActionKind.DiskMetrics,
                "system_disk_metrics",
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                "Disk and storage status");
            return new DeterministicIntentResult(route, 1.0, normalized, "DiskMetrics");
        }

        // 7. Operating System / Host Info
        if (IsOsInfoIntent(normalized))
        {
            var route = new DirectActionRoute(
                DirectActionKind.OsInfo,
                "system_os_info",
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                "Operating system information");
            return new DeterministicIntentResult(route, 1.0, normalized, "OsInfo");
        }

        // 8. Process List / Enumeration
        if (IsProcessIntent(normalized))
        {
            var route = new DirectActionRoute(
                DirectActionKind.ProcessList,
                "system_processes",
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                "Running process enumeration");
            return new DeterministicIntentResult(route, 1.0, normalized, "ProcessList");
        }

        return new DeterministicIntentResult(null, 0.0, normalized, null);
    }

    private static bool IsSystemReportIntent(string text)
    {
        string[] exactMatches =
        {
            "full system report", "system report", "system status", "full system status",
            "system diagnostic", "system diagnostics", "machine report", "hardware report",
            "full hardware report", "system specs", "hardware specs", "machine specs",
            "computer specs", "system info", "system information", "machine info", "hardware info",
            "what are my system specs", "what are the system specs", "show system info"
        };

        if (exactMatches.Contains(text, StringComparer.OrdinalIgnoreCase)) return true;

        if (Regex.IsMatch(text, @"^(?:what\s+(?:are|is)\s+(?:my\s+|the\s+)?)?(?:full\s+)?(?:system|machine|hardware)\s+(?:report|status|diagnostic|diagnostics|specs|info|information)$", RegexOptions.IgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsGpuIntent(string text)
    {
        // Explicitly exclude multi-step or non-telemetry requests (e.g. "train a model on gpu", "install nvidia driver")
        if (text.Contains("train", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("install", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("code", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("script", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool hasGpuEntity = Regex.IsMatch(text, @"\b(?:gpu|vram|graphics\s*card|video\s*card|nvidia|geforce|rtx|gtx|radeon)\b", RegexOptions.IgnoreCase);
        if (!hasGpuEntity) return false;

        // Telemetry words or query patterns
        bool hasTelemetryKeyword = Regex.IsMatch(text, @"\b(?:load|usage|utilization|util|temp|temperature|metrics|stats|status|memory|vram|specs|info|information|speed|clock|sensor|activity|power|details|doing|busy|healthy|model)\b", RegexOptions.IgnoreCase);
        bool isQuestionAboutGpu = Regex.IsMatch(text, @"^(?:what|how|show|get|display|is|confirm|tell|check|my)\b", RegexOptions.IgnoreCase) ||
                                  text.Equals("gpu", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("vram", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("gpu metrics", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("gpu load", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("gpu usage", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("gpu temp", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("gpu temperature", StringComparison.OrdinalIgnoreCase);

        return hasTelemetryKeyword || isQuestionAboutGpu;
    }

    private static bool IsCpuIntent(string text)
    {
        if (text.Contains("code", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("compile", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("script", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("program", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool hasCpuEntity = Regex.IsMatch(text, @"\b(?:cpu|processor|cores|intel|ryzen|core\s*count)\b", RegexOptions.IgnoreCase);
        if (!hasCpuEntity) return false;

        bool hasTelemetryKeyword = Regex.IsMatch(text, @"\b(?:load|usage|utilization|util|temp|temperature|metrics|stats|status|specs|info|information|speed|frequency|clock|cores|activity|busy|doing|healthy|details|model)\b", RegexOptions.IgnoreCase);
        bool isQuestionAboutCpu = Regex.IsMatch(text, @"^(?:what|how|show|get|display|is|confirm|tell|check|my)\b", RegexOptions.IgnoreCase) ||
                                  text.Equals("cpu", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("cpu load", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("cpu usage", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("cpu metrics", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("processor load", StringComparison.OrdinalIgnoreCase);

        return hasTelemetryKeyword || isQuestionAboutCpu;
    }

    private static bool IsMemoryIntent(string text)
    {
        bool hasMemoryEntity = Regex.IsMatch(text, @"\b(?:ram|memory|system\s*memory|physical\s*memory)\b", RegexOptions.IgnoreCase);
        if (!hasMemoryEntity) return false;

        // VRAM belongs to GPU
        if (text.Contains("vram", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("gpu memory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("video memory", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool hasTelemetryKeyword = Regex.IsMatch(text, @"\b(?:usage|used|load|utilization|util|status|free|available|total|installed|stats|metrics|left|info|information|busy|state|space)\b", RegexOptions.IgnoreCase);
        bool isQuestionAboutMemory = Regex.IsMatch(text, @"^(?:what|how\s+much|show|get|display|is|confirm|tell|check|free|used|available)\b", RegexOptions.IgnoreCase) ||
                                     text.Equals("ram", StringComparison.OrdinalIgnoreCase) ||
                                     text.Equals("memory", StringComparison.OrdinalIgnoreCase) ||
                                     text.Equals("memory usage", StringComparison.OrdinalIgnoreCase) ||
                                     text.Equals("ram usage", StringComparison.OrdinalIgnoreCase) ||
                                     text.Equals("free ram", StringComparison.OrdinalIgnoreCase);

        return hasTelemetryKeyword || isQuestionAboutMemory;
    }

    private static bool IsDiskIntent(string text)
    {
        bool hasDiskEntity = Regex.IsMatch(text, @"\b(?:disk|storage|drive|drives|hard\s*drive|ssd|hdd|c\s*drive)\b", RegexOptions.IgnoreCase);
        if (!hasDiskEntity) return false;

        bool hasTelemetryKeyword = Regex.IsMatch(text, @"\b(?:space|usage|used|load|status|free|available|drives|info|information|left|capacity|size|stats|metrics|full)\b", RegexOptions.IgnoreCase);
        bool isQuestionAboutDisk = Regex.IsMatch(text, @"^(?:what|how\s+much|show|get|display|is|confirm|tell|check|free)\b", RegexOptions.IgnoreCase) ||
                                   text.Equals("disk", StringComparison.OrdinalIgnoreCase) ||
                                   text.Equals("disk space", StringComparison.OrdinalIgnoreCase) ||
                                   text.Equals("storage info", StringComparison.OrdinalIgnoreCase) ||
                                   text.Equals("drives", StringComparison.OrdinalIgnoreCase);

        return hasTelemetryKeyword || isQuestionAboutDisk;
    }

    private static bool IsOsInfoIntent(string text)
    {
        bool hasOsEntity = Regex.IsMatch(text, @"\b(?:os|operating\s*system|windows\s*version|windows\s*edition|system\s*version)\b", RegexOptions.IgnoreCase);
        if (!hasOsEntity)
        {
            if (text.Equals("windows version", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("os version", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("what os is running", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("what os is this", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        bool hasOsQuery = Regex.IsMatch(text, @"\b(?:version|edition|build|running|running\s+on|installed|machine\s+running|type|info|information|name|details|is\s+this|is\s+it)\b", RegexOptions.IgnoreCase) ||
                          Regex.IsMatch(text, @"^(?:what|which|confirm|show|tell|check|get)\b", RegexOptions.IgnoreCase);

        return hasOsQuery;
    }

    private static bool IsProcessIntent(string text)
    {
        if (text.Contains("kill", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stop", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("terminate", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("start", StringComparison.OrdinalIgnoreCase))
        {
            // Process management / killing is an active operation, not a pure enumeration query
            return false;
        }

        bool hasProcessEntity = Regex.IsMatch(text, @"\b(?:processes|process\s*list|running\s*tasks|tasklist|running\s*apps|active\s*processes)\b", RegexOptions.IgnoreCase);
        if (!hasProcessEntity) return false;

        bool hasQueryContext = Regex.IsMatch(text, @"\b(?:how\s+many|what|list|show|get|enumerate|count|top|running|active|exist)\b", RegexOptions.IgnoreCase) ||
                               text.Equals("processes", StringComparison.OrdinalIgnoreCase) ||
                               text.Equals("running processes", StringComparison.OrdinalIgnoreCase) ||
                               text.Equals("process list", StringComparison.OrdinalIgnoreCase);

        return hasQueryContext;
    }

    private static DirectActionRoute? TryResolveAppLaunch(string rawMessage, string normalized)
    {
        // 1. Chrome / Browser with optional monitor or URL / search target
        var chromeMatch = Regex.Match(
            rawMessage,
            @"^(?:open|launch|start)\s+(?:chrome|browser|google\s+chrome)(?:\s+(?:on|to)\s+(?:monitor|display|screen)\s+(\d+))?(?:\s+(?:i\s+want\s+to\s+watch\s+|to\s+)(.+))?$",
            RegexOptions.IgnoreCase);

        if (chromeMatch.Success)
        {
            var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["app"] = "chrome"
            };

            if (chromeMatch.Groups[1].Success && int.TryParse(chromeMatch.Groups[1].Value, out var monitor))
            {
                args["monitor"] = monitor;
            }

            if (chromeMatch.Groups[2].Success && !string.IsNullOrWhiteSpace(chromeMatch.Groups[2].Value))
            {
                string target = chromeMatch.Groups[2].Value.Trim();
                args["target"] = target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? target
                    : $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(target)}";
            }

            return new DirectActionRoute(DirectActionKind.AppLaunch, "desktop_launch", args, "Launch web browser");
        }

        // 2. Known desktop applications
        var appMatch = Regex.Match(
            rawMessage,
            @"^(?:open|launch|start)\s+(?:app\s+|application\s+)?(notepad|calc|calculator|code|vscode|explorer|cmd|powershell|terminal|spotify|slack|discord|steam|paint|taskmgr|control|firefox|edge|blender|[a-zA-Z0-9_\-\.]+\.exe)(?:\s+(?:on|to)\s+(?:monitor|display|screen)\s+(\d+))?$",
            RegexOptions.IgnoreCase);

        if (appMatch.Success)
        {
            var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["app"] = appMatch.Groups[1].Value
            };

            if (appMatch.Groups[2].Success && int.TryParse(appMatch.Groups[2].Value, out var monitor))
            {
                args["monitor"] = monitor;
            }

            return new DirectActionRoute(DirectActionKind.AppLaunch, "desktop_launch", args, $"Launch application {args["app"]}");
        }

        // 3. Generic application launch: "open application <name>"
        var genericAppMatch = Regex.Match(
            rawMessage,
            @"^(?:open|launch|start)\s+(?:app|application)\s+([a-zA-Z0-9_\-\.]+)(?:\s+(?:on|to)\s+(?:monitor|display|screen)\s+(\d+))?$",
            RegexOptions.IgnoreCase);

        if (genericAppMatch.Success)
        {
            var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["app"] = genericAppMatch.Groups[1].Value
            };

            if (genericAppMatch.Groups[2].Success && int.TryParse(genericAppMatch.Groups[2].Value, out var monitor))
            {
                args["monitor"] = monitor;
            }

            return new DirectActionRoute(DirectActionKind.AppLaunch, "desktop_launch", args, $"Launch application {args["app"]}");
        }

        return null;
    }
}
