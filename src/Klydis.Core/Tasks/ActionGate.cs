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
        IReadOnlySet<string>? alreadyExecuted = null)
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
            string aliasKey = reqName.ToLowerInvariant();
            string? mapped = aliasKey switch
            {
                "system_cpu" => "system_cpu_info",
                "system_gpu" => "system_gpu_info",
                "system_mem" or "system_ram" => "system_memory",
                "system_disk" => "system_disks",
                "system_proc" => "system_processes",
                "top_processes" or "processes_top" => "system_top_processes",
                "find_process" or "search_processes" => "process_find",
                _ => null
            };
            if (mapped != null)
            {
                toolDef = tools.FirstOrDefault(t => string.Equals(t.Name, mapped, StringComparison.OrdinalIgnoreCase));
            }
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
        //    in it. NULL means the step declares NO restriction (existence-gated only).
        if (stepAllowedTools != null && !stepAllowedTools.Contains(toolDef.Name))
        {
            var allowed = string.Join(", ", stepAllowedTools
                .OrderBy(n => n, StringComparer.Ordinal));
            var repairJson = JsonSerializer.Serialize(new
            {
                repair = new
                {
                    type = "tool_not_allowed_for_step",
                    tool = toolDef.Name,
                    current_step = currentStep ?? "unknown",
                    allowed_tools = stepAllowedTools.OrderBy(n => n).ToList(),
                    allowed_retry = true
                }
            });
            return new ActionGateVerdict(false, ActionGateError.ToolNotAllowedForStep,
                repairJson,
                allowed, currentStep);
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
            Klydis.Core.Tasks.ToolSideEffectClassifier.Classify(request.Name) !=
                Klydis.Core.Tasks.ToolSideEffectLevel.ReadOnly &&
            Klydis.Core.Tasks.ToolSideEffectClassifier.Classify(request.Name) !=
                Klydis.Core.Tasks.ToolSideEffectLevel.Idempotent)
        {
            return new ActionGateVerdict(false, ActionGateError.ReplayDetected,
                $"Tool '{request.Name}' with IDENTICAL arguments already executed in this run. " +
                "Re-executing it would duplicate its side effects — do NOT repeat it. Read the " +
                "existing result or take a different action.",
                null, currentStep);
        }

        // 3d. Workspace boundary: when a workspace root is supplied, file-tool paths must stay
        // inside it. Absolute escapes and ../ traversal are rejected before execution.
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
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
