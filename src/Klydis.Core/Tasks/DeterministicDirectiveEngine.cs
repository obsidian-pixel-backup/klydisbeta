using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Klydis.Core.Chat;
using Klydis.Core.Orchestration;

namespace Klydis.Core.Tasks;

/// <summary>
/// Engine for generating deterministic execution directives when model produces
/// capability refusals or no-action narration, and for resolving direct tool executions.
/// </summary>
public static class DeterministicDirectiveEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Builds an execution authority directive when a model generates a false refusal.
    /// </summary>
    public static string BuildCapabilityRefusalDirective(TaskStep? currentStep)
    {
        string stepName = currentStep?.Title ?? "current objective";
        var allowedTools = currentStep?.AllowedTools?.ToList() ?? new List<string> { "system_report", "run_command" };

        var payload = new
        {
            directive = "EXECUTION_AUTHORITY_CONFIRMED",
            status = "refusal_rejected",
            message = "You have full execution authority for this environment. Tools and terminal access are active. Do not explain capability limits. Execute the required tool call now.",
            current_step = stepName,
            allowed_tools = allowedTools,
            action_required = true
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        return $"[RUNTIME EXECUTION DIRECTIVE]\n```json\n{json}\n```\nExecute the required tool immediately.";
    }

    /// <summary>
    /// Builds a compression repair directive when a model outputs text-only narration without an action.
    /// </summary>
    public static string BuildNoActionNarrationDirective(TaskStep? currentStep)
    {
        string stepName = currentStep?.Title ?? "current objective";
        var allowedTools = currentStep?.AllowedTools?.ToList() ?? new List<string> { "task_complete", "plan" };

        var payload = new
        {
            error = "NO_ACTION_NARRATION",
            status = "action_required",
            message = "Autonomous execution requires a tool call, plan update, or completion claim. Narration without an executable action is rejected.",
            current_step = stepName,
            allowed_tools = allowedTools,
            action_required = true
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        return $"[RUNTIME REPAIR DIRECTIVE]\n```json\n{json}\n```\nExecute the required action for the current step.";
    }

    /// <summary>
    /// Resolves unambiguous direct intents (e.g. CPU, RAM, GPU, OS, Process query) into deterministic tool calls.
    /// </summary>
    public static ToolCallRequest? TryResolveDirectAction(string? message, TaskStep? currentStep = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var resolution = DeterministicIntentResolver.Resolve(message);
        if (resolution.Route != null && resolution.Confidence >= 0.95)
        {
            var route = resolution.Route;
            var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (route.Arguments != null)
            {
                foreach (var (k, v) in route.Arguments)
                {
                    args[k] = v;
                }
            }

            return new ToolCallRequest(
                Name: route.ToolName,
                Arguments: (IDictionary<string, object>)(route.Arguments ?? new Dictionary<string, object>()));
        }

        // If current step is specific diagnostic without prompt
        if (currentStep != null && currentStep.AllowedTools != null && currentStep.AllowedTools.Count == 1)
        {
            string singleTool = currentStep.AllowedTools.First();
            if (singleTool.StartsWith("system_", StringComparison.OrdinalIgnoreCase))
            {
                return new ToolCallRequest(
                    Name: singleTool,
                    Arguments: new Dictionary<string, object>());
            }
        }

        return null;
    }
}
