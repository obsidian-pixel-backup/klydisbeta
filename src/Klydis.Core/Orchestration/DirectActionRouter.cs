using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Chat;

namespace Klydis.Core.Orchestration;

/// <summary>
/// The specific deterministic action kind resolved by <see cref="DirectActionRouter"/>.
/// </summary>
public enum DirectActionKind
{
    None = 0,
    SystemReport,
    CpuMetrics,
    GpuMetrics,
    MemoryMetrics,
    DiskMetrics,
    OsInfo,
    ProcessList,
    AppLaunch
}

/// <summary>
/// A resolved direct action ready for fast-path execution.
/// </summary>
public sealed record DirectActionRoute(
    DirectActionKind Kind,
    string ToolName,
    IDictionary<string, object> Arguments,
    string IntentDescription);

/// <summary>
/// The execution outcome of a direct action.
/// </summary>
public sealed record DirectExecutionResult(
    DirectActionKind Kind,
    string ToolName,
    bool Success,
    string FormattedResponse,
    string RawOutput,
    string? Error = null);

/// <summary>
/// Deterministic intent router that recognizes obvious operational telemetry queries
/// ("CPU load", "GPU load", "OS version", "full system report", "open chrome") and executes them
/// directly through runtime tools with zero LLM reasoning latency or capability hallucination risk.
/// </summary>
public static class DirectActionRouter
{
    private static readonly (Regex Pattern, DirectActionKind Kind, string ToolName, string Description)[] RouteTable =
    {
        // 1. Full System Report
        (new Regex(@"^(?:give\s+me\s+)?(?:a\s+)?(?:full\s+)?system\s+(?:report|status|diagnostic|diagnostics|info|information|specs)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.SystemReport, "system_report", "Full system report"),
        (new Regex(@"^(?:give\s+me\s+)?(?:a\s+)?(?:full\s+)?(?:machine|hardware)\s+report$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.SystemReport, "system_report", "Full hardware report"),

        // 2. CPU Metrics
        (new Regex(@"(?:what\s+is\s+(?:my\s+|the\s+)?(?:current\s+)?)?\bcpu\b\s+(?:load|usage|utilization|metrics|specs|frequency|clock|speed)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.CpuMetrics, "system_cpu_metrics", "CPU utilization metrics"),
        (new Regex(@"^(?:what\s+is\s+(?:my\s+|the\s+)?(?:current\s+)?)?processor\s+(?:load|usage|utilization)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.CpuMetrics, "system_cpu_metrics", "Processor utilization metrics"),

        // 3. GPU Metrics (matched before RAM so 'vram' is routed to GPU)
        (new Regex(@"(?:what\s+is\s+(?:my\s+|the\s+)?(?:current\s+)?)?\b(?:gpu|vram|graphics(?:\s+card)?)\b\s+(?:load|usage|utilization|metrics|temperature|temp|specs)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.GpuMetrics, "system_gpu_metrics", "GPU utilization and telemetry"),
        (new Regex(@"^(?:what\s+is\s+(?:my\s+|the\s+)?(?:current\s+)?)?\b(?:gpu|vram|graphics(?:\s+card)?)\b(?:\s+(?:temperature|temp|vram|specs|status))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.GpuMetrics, "system_gpu_metrics", "GPU status and telemetry"),

        // 4. Memory / RAM Metrics
        (new Regex(@"(?:what\s+is\s+(?:my\s+|the\s+)?(?:current\s+)?)?\b(?:ram|memory)\b\s+(?:usage|load|status|free|available|total|metrics|stats)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.MemoryMetrics, "system_memory_metrics", "Memory and RAM utilization"),
        (new Regex(@"^(?:how\s+much\s+)?\b(?:ram|memory)\b(?:\s+(?:is\s+)?(?:used|free|available|total|installed))?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.MemoryMetrics, "system_memory_metrics", "Memory availability"),
        (new Regex(@"^(?:free|used|available|total)\s+\b(?:ram|memory)\b$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.MemoryMetrics, "system_memory_metrics", "Memory status"),

        // 5. Disk / Storage Metrics
        (new Regex(@"(?:what\s+is\s+(?:my\s+|the\s+)?(?:current\s+)?)?\b(?:disk|storage|drive|hard\s+drive)\b\s+(?:space|usage|load|status|free|available|drives|info|information)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.DiskMetrics, "system_disk_metrics", "Disk and storage status"),
        (new Regex(@"^(?:how\s+much\s+)?\b(?:storage|drive|hard\s+drive|disk)\b\s+(?:space|info|status)(?:\s+(?:is\s+)?(?:free|left|available))?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.DiskMetrics, "system_disk_metrics", "Storage space status"),

        // 6. OS / Host Info
        (new Regex(@"(?:what\s+(?:os|operating\s+system)\s+(?:is\s+(?:the\s+|this\s+|my\s+)?machine\s+running(?:\s+on)?|am\s+i\s+running|is\s+installed|version))", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.OsInfo, "system_os_info", "Operating system information"),
        (new Regex(@"^(?:what\s+is\s+(?:my\s+|the\s+)?)?(?:os|windows)\s+version$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.OsInfo, "system_os_info", "Operating system version"),
        (new Regex(@"^(?:confirm|show|tell\s+me)\s+(?:what|which)\s+os(?:\s+is\s+running)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.OsInfo, "system_os_info", "Operating system confirmation"),

        // 7. Running Processes
        (new Regex(@"(?:how\s+many|what)\s+processes\s+(?:are\s+(?:currently\s+)?running|exist)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.ProcessList, "system_processes", "Running process list"),
        (new Regex(@"^(?:show|list|get)\s+(?:running\s+)?processes$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.ProcessList, "system_processes", "Running process enumeration"),

        // 8. Application Launch
        (new Regex(@"^(?:open|launch|start)\s+(?:chrome|browser|google\s+chrome)(?:\s+(?:on|to)\s+(?:monitor|display|screen)\s+(\d+))?(?:\s+(?:i\s+want\s+to\s+watch\s+|to\s+)(.+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.AppLaunch, "desktop_launch", "Launch web browser"),
        (new Regex(@"^(?:open|launch|start)\s+(?:app\s+|application\s+)?(notepad|calc|calculator|code|vscode|explorer|cmd|powershell|terminal|spotify|slack|discord|steam|paint|taskmgr|control|firefox|edge|[a-zA-Z0-9_\-\.]+\.exe)(?:\s+(?:on|to)\s+(?:monitor|display|screen)\s+(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.AppLaunch, "desktop_launch", "Launch desktop application"),
        (new Regex(@"^(?:open|launch|start)\s+(?:app|application)\s+([a-zA-Z0-9_\-\.]+)(?:\s+(?:on|to)\s+(?:monitor|display|screen)\s+(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         DirectActionKind.AppLaunch, "desktop_launch", "Launch desktop application")
    };

    /// <summary>
    /// Attempts to route a user message to a deterministic direct action.
    /// Returns null if the request requires complex LLM reasoning, code tasks, or multi-turn agent workflows.
    /// </summary>
    public static DirectActionRoute? TryRoute(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        string clean = message.Trim().TrimEnd('.', '?', '!', '\r', '\n');

        foreach (var (pattern, kind, toolName, desc) in RouteTable)
        {
            var match = pattern.Match(clean);
            if (match.Success)
            {
                var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                if (kind == DirectActionKind.AppLaunch)
                {
                    if (pattern.ToString().Contains("chrome|browser", StringComparison.OrdinalIgnoreCase))
                    {
                        args["app"] = "chrome";
                        if (match.Groups.Count > 1 && match.Groups[1].Success)
                        {
                            args["monitor"] = int.TryParse(match.Groups[1].Value, out var m) ? m : 1;
                        }
                        if (match.Groups.Count > 2 && match.Groups[2].Success && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
                        {
                            string target = match.Groups[2].Value.Trim();
                            args["target"] = target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? target
                                : $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(target)}";
                        }
                    }
                    else
                    {
                        args["app"] = match.Groups[1].Value;
                        if (match.Groups.Count > 2 && match.Groups[2].Success)
                        {
                            args["monitor"] = int.TryParse(match.Groups[2].Value, out var m) ? m : 1;
                        }
                    }
                }

                return new DirectActionRoute(kind, toolName, args, desc);
            }
        }

        return null;
    }

    /// <summary>
    /// Executes a direct action synchronously through the ToolExecutor and formats the output.
    /// </summary>
    public static async Task<DirectExecutionResult> ExecuteAsync(
        DirectActionRoute route,
        ToolExecutor toolExecutor,
        string sessionId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(toolExecutor);

        var request = new ToolCallRequest(route.ToolName, route.Arguments);
        var result = await toolExecutor.ExecuteToolAsync(request, sessionId, ct);

        string formatted;
        if (result.Success)
        {
            formatted = FormatResponse(route.Kind, result.Output, route.Arguments);
        }
        else
        {
            formatted = $"I was unable to retrieve {route.IntentDescription.ToLowerInvariant()}.\n\nError: {result.Error ?? result.Output}";
        }

        return new DirectExecutionResult(
            route.Kind,
            route.ToolName,
            result.Success,
            formatted,
            result.Output,
            result.Error);
    }

    private static string FormatResponse(DirectActionKind kind, string rawOutput, IDictionary<string, object> args)
    {
        return kind switch
        {
            DirectActionKind.SystemReport => rawOutput,
            DirectActionKind.CpuMetrics => $"{rawOutput}",
            DirectActionKind.GpuMetrics => $"{rawOutput}",
            DirectActionKind.MemoryMetrics => $"{rawOutput}",
            DirectActionKind.DiskMetrics => $"{rawOutput}",
            DirectActionKind.OsInfo => $"{rawOutput}",
            DirectActionKind.ProcessList => $"{rawOutput}",
            DirectActionKind.AppLaunch => rawOutput,
            _ => rawOutput
        };
    }
}
