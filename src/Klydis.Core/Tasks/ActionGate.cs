using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>run_command was called with another tool's name as the command.</summary>
    CommandDisguisedAsTool
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
    public const string CommandDisguisedAsToolCode = "MODEL_INVENTED_TOOL";

    /// <summary>The machine-searchable error code for a gate error.</summary>
    public static string ErrorCode(ActionGateError error) => error switch
    {
        ActionGateError.UnknownTool => UnknownToolCode,
        ActionGateError.ToolNotAllowedForStep => ToolNotAllowedForStepCode,
        ActionGateError.MissingRequiredArgument => MissingRequiredArgumentCode,
        ActionGateError.CommandDisguisedAsTool => CommandDisguisedAsToolCode,
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
        string? currentStep = null)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (registeredTools == null) throw new ArgumentNullException(nameof(registeredTools));
        var tools = registeredTools.ToList();

        // 1. Existence: the tool must be in the ACTUAL registered surface. This is the hard
        //    guard against hallucinated tools (design_website_designer, research_eyewitnessing,
        //    check_review): the model may never execute something that does not exist.
        var toolDef = tools.FirstOrDefault(t =>
            string.Equals(t.Name, request.Name, StringComparison.OrdinalIgnoreCase));
        if (toolDef == null)
        {
            var available = string.Join(", ", tools
                .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
            return new ActionGateVerdict(false, ActionGateError.UnknownTool,
                $"Tool '{request.Name}' does not exist in the registered tool surface. " +
                $"Only registered tools may be called. Registered tools: [{available}].",
                available, currentStep);
        }

        // 2. Step scoping: when the current step declares an allowed-tool set, the action must
        //    be in it. (Harness-control tools are always permitted by the caller's policy —
        //    StepToolPolicy unions them in — but the gate validates whatever it is given.)
        if (stepAllowedTools != null && stepAllowedTools.Count > 0 &&
            !stepAllowedTools.Contains(request.Name))
        {
            var allowed = string.Join(", ", stepAllowedTools
                .OrderBy(n => n, StringComparer.Ordinal));
            return new ActionGateVerdict(false, ActionGateError.ToolNotAllowedForStep,
                $"Tool '{request.Name}' is not permitted for the current step" +
                (string.IsNullOrWhiteSpace(currentStep) ? "." : $" '{currentStep}'.") +
                $" Allowed tools for this step: [{allowed}].",
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
            return new ActionGateVerdict(false, ActionGateError.MissingRequiredArgument,
                $"Tool '{request.Name}' is missing required argument(s): [{string.Join(", ", missing)}]. " +
                "Provide every required argument and retry.",
                null, currentStep);
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
