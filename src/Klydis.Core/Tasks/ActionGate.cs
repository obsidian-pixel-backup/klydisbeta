using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// The deterministic failure classes the <see cref="ActionGate"/> can return, mapped to
/// machine-searchable error codes. The model is NEVER allowed to create the impression that
/// a tool exists or executed merely by naming it — only these verdicts plus actual
/// <see cref="ToolExecutor"/> execution create tool state.
/// </summary>
public enum ActionGateError
{
    /// <summary>The named tool is not in the registered tool surface (a hallucinated tool).</summary>
    UnknownTool,

    /// <summary>The tool exists but is not permitted for the current step's obligation.</summary>
    ToolNotAllowedForStep,

    /// <summary>A required parameter is missing from the action's arguments.</summary>
    MissingRequiredArgument,

    /// <summary>A present argument violates the parameter's declared type or enum.</summary>
    InvalidArgument,

    /// <summary>run_command was called with another tool's name as the command.</summary>
    CommandDisguisedAsTool,

    /// <summary>A file-tool path escapes the task workspace root (absolute escape or ../ traversal).</summary>
    WorkspaceBoundaryViolation,

    /// <summary>The same non-read-only action already executed in this run — a replay after
    /// recovery would duplicate its side effects.</summary>
    ReplayDetected,

    /// <summary>task_complete was claimed while the run's completion eligibility is false
    /// (open plan items, unsatisfied verification, unresolved failures).</summary>
    PrematureCompletion
}

/// <summary>
/// The result of <see cref="ActionGate.Validate"/>: whether the action may execute, and when
/// rejected, the error, a concrete reason, the tools that WERE allowed, and the step the
/// action was validated against — all surfaced in the rejection feedback and diagnostics.
/// </summary>
public readonly record struct ActionGateVerdict(
    bool Allowed,
    ActionGateError? Error,
    string? Reason,
    string? AllowedToolsSummary,
    string? CurrentStep);

/// <summary>
/// The deterministic Action Gate (P1.7a). Every model action passes here BEFORE
/// <see cref="ToolExecutor"/> executes it. Validation is a RUNTIME decision — prompt
/// instructions are never the enforcement boundary:
///
///   tool exists in the registered surface        (MODEL_INVENTED_TOOL)
///   tool is permitted by the current step        (ACTION_NOT_ALLOWED_FOR_STEP)
///     (null step set = no restriction; empty set = NO tools permitted)
///   required arguments are present               (ACTION_SCHEMA_INVALID)
///   run_command is not disguising another tool   (MODEL_INVENTED_TOOL)
///
/// Anti-simulation invariants enforced by this layer:
///   - Only ToolExecutor execution creates tool-result state. A model's narrative claim of a
///     result ("the search returned...") is TEXT, never context the harness trusts.
///   - Step completion requires evidence (plan mutations / the fail-closed completion gate),
///     so a model can never advance state or verify work by narration alone.
/// </summary>
public static class ActionGate
{
    public const string UnknownToolCode = "MODEL_INVENTED_TOOL";
    public const string ToolNotAllowedForStepCode = "ACTION_NOT_ALLOWED_FOR_STEP";
    public const string MissingRequiredArgumentCode = "ACTION_SCHEMA_INVALID";
    public const string InvalidArgumentCode = "ACTION_SCHEMA_INVALID";
    public const string CommandDisguisedAsToolCode = "MODEL_INVENTED_TOOL";
    public const string WorkspaceBoundaryViolationCode = "ACTION_OUTSIDE_WORKSPACE";
    public const string ReplayDetectedCode = "REPLAY_DETECTED";
    public const string PrematureCompletionCode = "COMPLETION_NOT_ELIGIBLE";

    /// <summary>The machine-searchable error code for a gate error.</summary>
    public static string ErrorCode(ActionGateError error) => error switch
    {
        ActionGateError.UnknownTool => UnknownToolCode,
        ActionGateError.ToolNotAllowedForStep => ToolNotAllowedForStepCode,
        ActionGateError.MissingRequiredArgument => MissingRequiredArgumentCode,
        ActionGateError.InvalidArgument => InvalidArgumentCode,
        ActionGateError.CommandDisguisedAsTool => CommandDisguisedAsToolCode,
        ActionGateError.WorkspaceBoundaryViolation => WorkspaceBoundaryViolationCode,
        ActionGateError.ReplayDetected => ReplayDetectedCode,
        ActionGateError.PrematureCompletion => PrematureCompletionCode,
        _ => "ACTION_GATE_UNKNOWN"
    };

    /// <summary>
    /// Validates an action against the registered tool surface and (when provided) the
    /// current step's allowed-tool set. Never throws. A verdict of Allowed=true is the only
    /// path to execution.
    /// </summary>
    public static ActionGateVerdict Validate(
        ToolCallRequest request,
        IEnumerable<ToolDefinition> registeredTools,
        IReadOnlySet<string>? stepAllowedTools = null,
        string? currentStep = null,
        string? workspaceRoot = null,
        IReadOnlySet<string>? alreadyExecuted = null,
        Klydis.Core.Workspace.AgentWorkspaceContext? workspaceContext = null)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (registeredTools == null) throw new ArgumentNullException(nameof(registeredTools));
        var tools = registeredTools.ToList();

        // 1. Existence & Semantic Alias Resolution:
        string reqName = request.Name;
        var toolDef = tools.FirstOrDefault(t =>
            string.Equals(t.Name, reqName, StringComparison.OrdinalIgnoreCase));

        if (toolDef == null)
        {
            string? mapped = ResolveToolAlias(reqName);
            if (mapped != null)
            {
                toolDef = tools.FirstOrDefault(t => string.Equals(t.Name, mapped, StringComparison.OrdinalIgnoreCase));
                if (toolDef != null)
                {
                    request = new ToolCallRequest(toolDef.Name, request.Arguments);
                }
            }
        }

        // 1b. ARGUMENT ALIASES — fold model-variant parameter names onto the canonical ones
        // BEFORE every downstream check (required-ness, types, replay identity). This mirrors
        // exactly the fallbacks ToolExecutor already applies at execution time (e.g.
        // GetStringArg(..., "pattern") ?? GetStringArg(..., "query")): a call that WOULD have
        // executed must not be rejected by the gate for the very key spelling the executor
        // tolerates. Small models (qwen3.5, mistral) routinely pass 'query' for 'pattern',
        // 'section' for 'heading', 'url' for 'document' — previously a perfectly good call
        // died as ACTION_SCHEMA_INVALID and the tool got blocked after 2 tries.
        if (toolDef != null)
        {
            request = CanonicalizeArguments(request, toolDef.Name);
        }

        if (toolDef == null)
        {
            var available = string.Join(", ", tools
                .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
            var repairJson = JsonSerializer.Serialize(new
            {
                repair = new
                {
                    type = "unknown_tool",
                    tool = request.Name,
                    available_tools = tools.Select(t => t.Name).OrderBy(n => n).ToList(),
                    allowed_retry = true
                }
            });
            return new ActionGateVerdict(false, ActionGateError.UnknownTool,
                repairJson,
                available, currentStep);
        }

        // 2. Step scoping: a NON-NULL allowed-tool set is authoritative — the action must be
        //    in it or satisfy equivalent capability compatibility. NULL means the step declares NO restriction.
        bool isAllowed = stepAllowedTools == null || stepAllowedTools.Contains(toolDef.Name);

        // Capability compatibility fallback:
        // If step allows run_command and model requested a specialized read-only diagnostic or inspection tool, permit it!
        if (!isAllowed && stepAllowedTools != null)
        {
            if (stepAllowedTools.Contains("run_command") && CapabilityResolver.IsSpecializedDiagnosticTool(toolDef.Name))
            {
                isAllowed = true;
            }
            else if ((stepAllowedTools.Contains("write_file") || stepAllowedTools.Contains("edit_file") || stepAllowedTools.Contains("run_command")) &&
                     (toolDef.Name is "read_file" or "list_directory" or "search_files" or "file_exists"))
            {
                isAllowed = true;
            }
        }

        if (!isAllowed)
        {
            var candidateAlternatives = stepAllowedTools!
                .OrderByDescending(CapabilityResolver.GetToolPriority)
                .ToList();
            string recommended = candidateAlternatives.FirstOrDefault() ?? "plan";
            string validToolsSummary = string.Join(", ", candidateAlternatives);
            string guidance = $"The requested action '{toolDef.Name}' is blocked by the current step. Valid tools: [{validToolsSummary}]. Recommended: '{recommended}'. Do not repeat the blocked call. Choose one valid tool.";

            var repairJson = JsonSerializer.Serialize(new
            {
                repair = new
                {
                    type = "tool_not_allowed_for_step",
                    tool = toolDef.Name,
                    current_step = currentStep ?? "unknown",
                    allowed_tools = candidateAlternatives,
                    recommended_alternative = recommended,
                    guidance = guidance,
                    allowed_retry = true
                }
            });
            return new ActionGateVerdict(false, ActionGateError.ToolNotAllowedForStep,
                repairJson,
                validToolsSummary, currentStep);
        }

        // 3. Schema: every required parameter must be present with a non-empty value. A call
        //    missing required arguments is rejected before execution, not after.
        var missing = toolDef.Parameters
            .Where(p => p.Required && !HasNonEmptyArgument(request.Arguments, p.Name))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            var repairJson = JsonSerializer.Serialize(new
            {
                repair = new
                {
                    type = "missing_required_argument",
                    tool = toolDef.Name,
                    missing = missing,
                    must_change = true,
                    allowed_retry = true
                }
            });
            return new ActionGateVerdict(false, ActionGateError.MissingRequiredArgument,
                repairJson,
                null, currentStep);
        }

        // 3b. Type, enum, and template placeholder validation:
        var typeErrors = new List<string>();
        foreach (var p in toolDef.Parameters)
        {
            var val = FindArgumentValue(request.Arguments, p.Name);
            if (val == null) continue;

            if (val is string strVal && ((strVal.StartsWith("<") && strVal.EndsWith(">")) || (strVal.StartsWith("[") && strVal.EndsWith("]"))) && strVal.Length > 2)
            {
                typeErrors.Add($"{p.Name} (received placeholder template '{strVal}' — provide a real resolved value)");
            }
            else if (!IsTypeCompatible(val, p.Type))
            {
                typeErrors.Add($"{p.Name} (expected type {p.Type})");
            }
            else if (p.Enum != null && p.Enum.Length > 0 && !IsEnumValue(val, p.Enum))
            {
                typeErrors.Add($"{p.Name} (must be one of: {string.Join(", ", p.Enum)})");
            }
        }
        if (typeErrors.Count > 0)
        {
            var repairJson = JsonSerializer.Serialize(new
            {
                repair = new
                {
                    type = "invalid_argument_values",
                    tool = toolDef.Name,
                    errors = typeErrors,
                    must_change = true,
                    allowed_retry = true
                }
            });
            return new ActionGateVerdict(false, ActionGateError.InvalidArgument,
                repairJson,
                null, currentStep);
        }

        // 3c. REPLAY DETECTION: an action that already executed in this run must not execute
        // again when it carries side effects — a recovery loop must never duplicate a command
        // or destructive call. ReadOnly tools are exempt (re-reading is safe and legitimate).
        // Identity is tool + canonicalized arguments, so identical calls across turns,
        // context resets and protocol fallbacks are the same logical action.
        if (alreadyExecuted != null &&
            alreadyExecuted.Contains(ComputeReplayKey(request)) &&
            !string.Equals(request.Name, "plan", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Name, "task_progress", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Name, "check_message_queue", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Name, "incorporate_queued_message", StringComparison.OrdinalIgnoreCase) &&
            // Args are passed so run_command is classified by its COMMAND TEXT: a read-only
            // diagnostic (Get-*, systeminfo, git status, ...) is exempt from replay (safe to
            // re-run for a fresh reading — the observed 9x REPLAY_DETECTED loop on read-only
            // commands), while a mutating command stays ExternalSideEffect and is blocked.
            Klydis.Core.Tasks.ToolSideEffectClassifier.Classify(request.Name, request.Arguments) !=
                Klydis.Core.Tasks.ToolSideEffectLevel.ReadOnly &&
            Klydis.Core.Tasks.ToolSideEffectClassifier.Classify(request.Name, request.Arguments) !=
                Klydis.Core.Tasks.ToolSideEffectLevel.Idempotent)
        {
            return new ActionGateVerdict(false, ActionGateError.ReplayDetected,
                $"Tool '{request.Name}' with IDENTICAL arguments already executed in this run. " +
                "Re-executing it would duplicate its side effects — do NOT repeat it. Read the " +
                "existing result or take a different action.",
                null, currentStep);
        }

        // 3d. Workspace boundary: when workspace context or root is supplied, file-tool paths must stay
        // inside it. Absolute escapes, ../ traversal, and restricted paths are rejected before execution.
        if (workspaceContext != null)
        {
            string? boundary = Klydis.Core.Tasks.WorkspaceBoundaryValidator.Validate(
                request.Name, request.Arguments, workspaceContext);
            if (boundary != null)
            {
                return new ActionGateVerdict(false, ActionGateError.WorkspaceBoundaryViolation,
                    boundary, null, currentStep);
            }
        }
        else if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            string? boundary = Klydis.Core.Tasks.WorkspaceBoundaryValidator.Validate(
                request.Name, request.Arguments, workspaceRoot);
            if (boundary != null)
            {
                return new ActionGateVerdict(false, ActionGateError.WorkspaceBoundaryViolation,
                    boundary, null, currentStep);
            }
        }

        // 4. Command disguise: run_command called with another registered tool's name as the
        //    command ("run_command(\"search_web ...\")") is the export's exact misuse — the
        //    model tried to invoke a tool through the shell. The tool must be called directly.
        if (string.Equals(request.Name, "run_command", StringComparison.OrdinalIgnoreCase))
        {
            string? command = TryReadStringArg(request.Arguments, "command");
            string? firstToken = FirstCommandToken(command);
            if (!string.IsNullOrEmpty(firstToken))
            {
                var disguised = tools.FirstOrDefault(t =>
                    string.Equals(t.Name, firstToken, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(t.Name, "run_command", StringComparison.OrdinalIgnoreCase));
                if (disguised != null)
                {
                    return new ActionGateVerdict(false, ActionGateError.CommandDisguisedAsTool,
                        $"You attempted to invoke tool '{disguised.Name}' as a shell command inside run_command. " +
                        $"'{disguised.Name}' is a registered TOOL — call it directly as a tool action, never " +
                        "through run_command.",
                        null, currentStep);
                }
            }
        }

        return new ActionGateVerdict(true, null, null, null, currentStep);
    }

    /// <summary>
    /// Maps a model-invented tool-name variant to its registered canonical name, or null
    /// when no alias exists. Kept public so rejection feedback can resolve the schema of an
    /// aliased tool exactly as the gate did.
    /// </summary>
    public static string? ResolveToolAlias(string name)
    {
        string aliasKey = name.ToLowerInvariant();
        return aliasKey switch
        {
            "system_cpu" or "system_cpu_usage" or "system_cpu_metrics" or "cpu_info" or "cpu_usage" => "system_cpu_info",
            "system_gpu" or "system_gpu_usage" or "system_gpu_metrics" or "gpu_info" or "gpu_usage" => "system_gpu_info",
            "system_mem" or "system_ram" or "system_memory_metrics" or "ram_info" or "memory_info" => "system_memory",
            "system_disk" or "system_disk_metrics" or "disk_info" or "disk_space" => "system_disks",
            "system_os_info" or "os_info" or "windows_info" => "system_os",
            "system_proc" or "system_process" or "process_list" => "system_processes",
            "top_processes" or "processes_top" or "system_cpu_processes" or "cpu_processes" or "system_cpu_procs" or "system_top_processes" => "system_top_processes",
            "find_process" or "search_processes" or "get_process" => "process_find",
            "system_temp" or "system_temperature" => "system_temperatures",
            "system_time" or "system_uptime" => "system_uptime",
            "hardware_report" or "system_hardware" => "system_hardware_report",
            "software_report" or "system_software" => "system_software_report",
            "system_info" or "get_system_info" => "system_report",
            "list_dir" => "list_directory",
            "search_dir" or "find_files" => "search_files",
            "str_replace" or "replace_text" => "replace_lines",
            _ => null
        };
    }

    /// <summary>
    /// Parameter-name aliases per tool, sourced from the SAME fallbacks the ToolExecutor's
    /// GetStringArg chains apply at execution time. The gate and the executor must agree on
    /// which key spellings are acceptable — otherwise a call the executor would have happily
    /// executed dies at the gate as ACTION_SCHEMA_INVALID.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> ArgumentAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search_files"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["pattern"] = new[] { "query", "text", "term" }
        },
        ["find_on_page"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["pattern"] = new[] { "query", "text" },
            ["document"] = new[] { "url", "document_id" }
        },
        ["get_section"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["heading"] = new[] { "section" },
            ["document"] = new[] { "url", "document_id" }
        },
        ["get_links"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["document"] = new[] { "url", "document_id" }
        },
        ["get_table"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["document"] = new[] { "url", "document_id" }
        },
        ["get_metadata"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["document"] = new[] { "url", "document_id" }
        },
        ["process_find"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new[] { "query", "process_name" }
        }
    };

    /// <summary>
    /// Folds alias-spelled arguments onto their canonical parameter names. Only applied when
    /// the canonical key is ABSENT (a real value under the canonical name always wins). The
    /// executor's GetStringArg fallbacks tolerate both spellings, so the aliased key is
    /// dropped after folding to keep replay hashes canonical.
    /// </summary>
    private static ToolCallRequest CanonicalizeArguments(ToolCallRequest request, string toolName)
    {
        if (request.Arguments == null || request.Arguments.Count == 0)
        {
            return request;
        }
        if (!ArgumentAliases.TryGetValue(toolName, out var aliases))
        {
            return request;
        }

        bool changed = false;
        var canonical = new Dictionary<string, object>(
            request.Arguments, StringComparer.OrdinalIgnoreCase);
        foreach (var (canonicalName, aliasList) in aliases)
        {
            bool hasCanonical = canonical.Keys.Any(k =>
                string.Equals(k, canonicalName, StringComparison.OrdinalIgnoreCase));
            if (hasCanonical) continue;

            string? matchedAlias = null;
            foreach (var alias in aliasList)
            {
                var hit = canonical.Keys.FirstOrDefault(k =>
                    string.Equals(k, alias, StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                {
                    matchedAlias = hit;
                    break;
                }
            }
            if (matchedAlias == null) continue;

            canonical[canonicalName] = canonical[matchedAlias];
            canonical.Remove(matchedAlias);
            changed = true;
        }

        return changed
            ? new ToolCallRequest(request.Name, canonical)
            : request;
    }

    /// <summary>
    /// Computes a stable action identity for diagnostics: task, turn context, tool and
    /// canonicalized arguments. Used so a rejection and any later execution of the same
    /// action can be correlated across logs.
    /// </summary>
    public static string ComputeActionId(ToolCallRequest request, string? taskId, string? runId, int turnOrdinal)
    {
        var argsHash = ComputeArgsHash(request.Arguments);
        return $"A-{taskId ?? "?"}-{runId ?? "?"}-{turnOrdinal}-{request.Name}-{argsHash}";
    }

    /// <summary>
    /// The replay identity of an action: tool + canonicalized arguments. Intentionally does
    /// NOT include the turn/generation, so the same logical action across a context reset or
    /// protocol fallback is recognized as the same action — the idempotency key recovery
    /// uses to avoid re-executing side effects.
    /// </summary>
    public static string ComputeReplayKey(ToolCallRequest request)
        => request.Name + "|" + ComputeArgsHash(request.Arguments);

    private static bool HasNonEmptyArgument(IDictionary<string, object>? args, string paramName)
    {
        if (args == null) return false;
        foreach (var kvp in args)
        {
            if (!string.Equals(kvp.Key, paramName, StringComparison.OrdinalIgnoreCase)) continue;
            var val = ToolExecutor.UnwrapJsonElement(kvp.Value);
            if (val == null) return false;
            return !string.IsNullOrWhiteSpace(val.ToString());
        }
        return false;
    }

    private static object? FindArgumentValue(IDictionary<string, object>? args, string paramName)
    {
        if (args == null) return null;
        foreach (var kvp in args)
        {
            if (!string.Equals(kvp.Key, paramName, StringComparison.OrdinalIgnoreCase)) continue;
            return ToolExecutor.UnwrapJsonElement(kvp.Value);
        }
        return null;
    }

    /// <summary>
    /// Generates a compact structured error string (avoiding large verbose explanations).
    /// </summary>
    public static string FormatCompactError(string toolName, ActionGateError error, int attempt = 1, bool retryable = false, string? detail = null)
    {
        string code = ErrorCode(error);
        return $"TOOL_FAILED\ntool={toolName}\ncode={code}\nattempt={attempt}\nretryable={retryable.ToString().ToLowerInvariant()}" +
               (string.IsNullOrWhiteSpace(detail) ? "" : $"\ndetail={detail}");
    }

    /// <summary>
    /// Whether an argument value is compatible with a ToolParameter type declaration. Handles
    /// the primitive shapes the parsers produce (string / JsonElement string / numbers /
    /// booleans). A JSON string that holds a number stays a string — type mismatches like
    /// path=123 (number where string declared) are what this rejects.
    /// </summary>
    private static bool IsTypeCompatible(object? val, string declaredType)
    {
        if (val == null) return true; // absence is the required-args check's job
        string type = declaredType.ToLowerInvariant();
        switch (type)
        {
            case "string":
                return val is string || (val is JsonElement je && je.ValueKind == JsonValueKind.String);
            case "integer":
                if (val is int or long or short or byte) return true;
                if (val is double d && Math.Abs(d % 1) < double.Epsilon) return true;
                if (val is float f && Math.Abs(f % 1) < float.Epsilon) return true;
                if (val is JsonElement jn && jn.ValueKind == JsonValueKind.Number && jn.TryGetInt64(out _)) return true;
                if (val is string strInt)
                {
                    var cleaned = strInt.Trim().TrimEnd('%');
                    if (long.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _) ||
                        (double.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedD) && Math.Abs(parsedD % 1) < double.Epsilon))
                    {
                        return true;
                    }
                }
                return false;
            case "number":
                if (val is int or long or double or float or decimal or short or byte) return true;
                if (val is JsonElement num && num.ValueKind == JsonValueKind.Number) return true;
                if (val is string strNum)
                {
                    var cleaned = strNum.Trim().TrimEnd('%');
                    if (double.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        return true;
                    }
                }
                return false;
            case "boolean":
                if (val is bool) return true;
                if (val is JsonElement jb && (jb.ValueKind == JsonValueKind.True || jb.ValueKind == JsonValueKind.False)) return true;
                if (val is string strBool && bool.TryParse(strBool.Trim(), out _)) return true;
                return false;
            default:
                // Unknown declared type: be permissive rather than wrongly rejecting.
                return true;
        }
    }

    private static bool IsEnumValue(object? val, string[] allowed)
    {
        string? text = val is string s ? s : val?.ToString();
        if (string.IsNullOrEmpty(text)) return false;
        return allowed.Any(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryReadStringArg(IDictionary<string, object>? args, string key)
    {
        if (args == null) return null;
        foreach (var kvp in args)
        {
            if (!string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            return ToolExecutor.UnwrapJsonElement(kvp.Value)?.ToString();
        }
        return null;
    }

    private static string? FirstCommandToken(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.Trim();
        // Strip a leading quote and trailing punctuation so `search_web("...")` yields
        // search_web as the token.
        trimmed = trimmed.TrimStart('"', '\'', '(', ' ');
        int end = trimmed.IndexOfAny(new[] { ' ', '\t', '"', '\'', '(', ')' });
        return end < 0 ? trimmed : trimmed.Substring(0, end);
    }

    private static string ComputeArgsHash(IDictionary<string, object>? args)
    {
        if (args == null || args.Count == 0) return "none";
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in args)
        {
            var val = ToolExecutor.UnwrapJsonElement(kvp.Value)?.ToString() ?? "";
            sorted[kvp.Key] = val.Trim();
        }
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(sorted))))
            [..12].ToLowerInvariant();
    }
}
