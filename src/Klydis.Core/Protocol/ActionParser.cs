using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Protocol;

/// <summary>
/// Authoritative implementation of <see cref="IActionParser"/>.
/// Parses, normalizes, validates, and protects against tool hallucinations and simulated execution.
/// </summary>
public class ActionParser : IActionParser
{
    private static readonly Dictionary<string, string> ToolAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["execute_command"] = "run_command",
        ["run_shell"] = "run_command",
        ["terminal_command"] = "run_command",
        ["shell"] = "run_command",
        ["powershell"] = "run_command",
        ["cmd"] = "run_command",
        ["powershell_command"] = "run_command",
        ["bash"] = "run_command",
        ["sh"] = "run_command",
        ["exec"] = "run_command",
        ["read"] = "read_file",
        ["write"] = "write_file",
        ["edit"] = "edit_file",
        ["ls"] = "list_directory",
        ["dir"] = "list_directory",
        ["find"] = "search_files",
        ["grep"] = "search_files",
        ["search"] = "search_web",
        ["google"] = "search_web",
        ["fetch"] = "crawl_url",
        ["scrape"] = "crawl_url"
    };

    private static readonly HashSet<string> HallucinatedToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "psudo", "sysinfo", "nvmax", "syslog", "os.info", "os.system",
        "system_info_raw", "get_cpu", "get_gpu", "get_ram", "get_os",
        "fetch_system_metrics", "python_exec", "run_python", "eval"
    };

    private static readonly Regex HallucinatedExecutionRegex = new(
        @"(?:from\s+os\s+import\s+sysinfo|import\s+(?:sysinfo|psudo|nvmax|syslog)|\b(?:psudo|sysinfo|nvmax|syslog)\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<ActionParser>? _logger;

    public ActionParser(ILogger<ActionParser>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ActionParseResult Parse(string? response, IReadOnlyList<ToolDefinition>? availableTools = null)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return new ActionParseResult(ActionParseStatus.NoAction, Array.Empty<ToolCallRequest>(), Array.Empty<CanonicalAction>());
        }

        string cleanResponse = OutputSanitizer.StripThinkingBlocks(response);
        if (string.IsNullOrWhiteSpace(cleanResponse))
        {
            return new ActionParseResult(ActionParseStatus.NoAction, Array.Empty<ToolCallRequest>(), Array.Empty<CanonicalAction>());
        }

        // 1. Detect hallucinated pseudo-execution patterns in plain text / markdown
        if (HallucinatedExecutionRegex.IsMatch(cleanResponse))
        {
            var match = HallucinatedExecutionRegex.Match(cleanResponse).Value;
            string repair = $"[Protocol Violation: Invented execution construct detected '{match}'. You do NOT execute commands via fake python modules or pseudo-tools. You MUST call 'run_command' with argument {{\"command\": \"...\"}}.]";
            return new ActionParseResult(
                ActionParseStatus.InvalidToolCall,
                Array.Empty<ToolCallRequest>(),
                Array.Empty<CanonicalAction>(),
                RejectionReason: $"Invented execution construct '{match}' is forbidden.",
                SuggestedRepair: repair);
        }

        // 2. Parse canonical actions and raw tool call requests
        var rawCalls = ActionDialectParser.ParseAll(cleanResponse, _logger);
        var canonicalActions = ActionDialectParser.ParseCanonical(cleanResponse, _logger);

        if (rawCalls.Count == 0)
        {
            // Check if model attempted a tool tag that failed to parse
            if (Regex.IsMatch(cleanResponse, @"<\|?tool_calls?\|?>|<function\b|<antml:invoke", RegexOptions.IgnoreCase))
            {
                string repair = "[Tool Error: Failed to parse tool call syntax. Use standard tool format: <tool_call><function=run_command><parameter=command>powershell command</parameter></function></tool_call> or <tool_call>{\"name\":\"run_command\",\"arguments\":{\"command\":\"...\"}}</tool_call>]";
                return new ActionParseResult(
                    ActionParseStatus.Malformed,
                    Array.Empty<ToolCallRequest>(),
                    Array.Empty<CanonicalAction>(),
                    RejectionReason: "Malformed tool call structure.",
                    SuggestedRepair: repair);
            }

            return new ActionParseResult(ActionParseStatus.NoAction, Array.Empty<ToolCallRequest>(), Array.Empty<CanonicalAction>());
        }

        // 3. Normalize tool aliases and check for unknown/hallucinated tools
        var normalizedCalls = new List<ToolCallRequest>(rawCalls.Count);
        var availableNames = availableTools != null && availableTools.Count > 0
            ? availableTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var call in rawCalls)
        {
            string targetName = call.Name;
            var args = call.Arguments != null ? new Dictionary<string, object>(call.Arguments, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object>();

            // Check if explicitly hallucinated
            if (HallucinatedToolNames.Contains(targetName))
            {
                string repair = $"[Tool Error: Unknown tool '{targetName}'. Do not invent tool names or simulate execution. To execute PowerShell commands on the system, call 'run_command' with argument {{\"command\": \"...\"}}.]";
                return new ActionParseResult(
                    ActionParseStatus.InvalidToolCall,
                    Array.Empty<ToolCallRequest>(),
                    canonicalActions,
                    RejectionReason: $"Tool '{targetName}' does not exist.",
                    SuggestedRepair: repair);
            }

            // Apply tool aliasing
            if (ToolAliases.TryGetValue(targetName, out var canonicalAlias))
            {
                targetName = canonicalAlias;
                // Normalize argument names for run_command if needed
                if (canonicalAlias == "run_command")
                {
                    if (!args.ContainsKey("command") && args.TryGetValue("script", out var scriptVal))
                        args["command"] = scriptVal;
                    else if (!args.ContainsKey("command") && args.TryGetValue("cmd", out var cmdVal))
                        args["command"] = cmdVal;
                    else if (!args.ContainsKey("command") && args.TryGetValue("code", out var codeVal))
                        args["command"] = codeVal;
                }
            }

            // Check if available tools are known and targetName is missing
            if (availableNames != null && !availableNames.Contains(targetName) && !targetName.Equals("task_complete", StringComparison.OrdinalIgnoreCase) && !targetName.Equals("plan", StringComparison.OrdinalIgnoreCase))
            {
                string validList = string.Join(", ", availableNames);
                string repair = $"[Tool Error: Tool '{targetName}' is not registered. Available tools: [{validList}]. Call 'run_command' to run shell commands.]";
                return new ActionParseResult(
                    ActionParseStatus.InvalidToolCall,
                    Array.Empty<ToolCallRequest>(),
                    canonicalActions,
                    RejectionReason: $"Tool '{targetName}' is not registered.",
                    SuggestedRepair: repair);
            }

            normalizedCalls.Add(new ToolCallRequest(targetName, args));
        }

        // 4. Classify status based on canonical action types
        if (canonicalActions.Any(a => a.Type == CanonicalActionType.CompletionClaim) ||
            normalizedCalls.Any(c => c.Name.Equals("task_complete", StringComparison.OrdinalIgnoreCase)))
        {
            return new ActionParseResult(ActionParseStatus.CompletionClaim, normalizedCalls, canonicalActions);
        }

        if (canonicalActions.Any(a => a.Type == CanonicalActionType.Replan) ||
            normalizedCalls.Any(c => c.Name.Equals("plan", StringComparison.OrdinalIgnoreCase)))
        {
            return new ActionParseResult(ActionParseStatus.Replan, normalizedCalls, canonicalActions);
        }

        return new ActionParseResult(ActionParseStatus.ValidToolCall, normalizedCalls, canonicalActions);
    }
}
