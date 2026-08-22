using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

public sealed record RecoveryRecommendation(
    string Status,
    string Reason,
    int AttemptCount,
    string Tool,
    string? FailureClass,
    IReadOnlyList<string> RecommendedRecovery,
    IReadOnlyList<string> AlternativeTools,
    string GuidanceMessage);

/// <summary>
/// Recovery controller that transforms passive duplicate blocks and execution failures
/// into structured, actionable recovery directives for the agent.
/// </summary>
public static class RecoveryController
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Builds an actionable recovery response payload for duplicate or blocked tool invocations.
    /// </summary>
    public static string BuildRecoveryPayload(
        string toolName,
        int attemptCount,
        string? failureClass = null,
        IDictionary<string, object?>? arguments = null,
        string? cachedResult = null)
    {
        var alternatives = GetAlternativeTools(toolName, failureClass, arguments);
        var recoveries = GetRecoverySteps(toolName, failureClass);

        string guidance = GetGuidanceText(toolName, attemptCount, alternatives);

        var recommendation = new RecoveryRecommendation(
            Status: "blocked",
            Reason: "repeated_failure",
            AttemptCount: attemptCount,
            Tool: toolName,
            FailureClass: failureClass ?? "REPEATED_ATTEMPT",
            RecommendedRecovery: recoveries,
            AlternativeTools: alternatives,
            GuidanceMessage: guidance);

        string jsonSummary = JsonSerializer.Serialize(recommendation, JsonOptions);

        if (!string.IsNullOrWhiteSpace(cachedResult))
        {
            string truncatedCached = cachedResult.Length > 1500
                ? cachedResult.Substring(0, 1500) + "\n...[cached output truncated]..."
                : cachedResult;

            return $"[STRUCTURED RECOVERY DIRECTIVE]\n```json\n{jsonSummary}\n```\n\n--- CACHED RESULT FROM PRIOR ATTEMPT ---\n{truncatedCached}";
        }

        return $"[STRUCTURED RECOVERY DIRECTIVE]\n```json\n{jsonSummary}\n```";
    }

    public static IReadOnlyList<string> GetAlternativeTools(string toolName, string? failureClass, IDictionary<string, object?>? arguments)
    {
        var lower = toolName.ToLowerInvariant();

        if (lower == "list_directory")
        {
            return new[] { "search_files", "read_file", "run_command" };
        }

        if (lower == "run_command")
        {
            if (arguments != null && arguments.TryGetValue("command", out var cmdObj))
            {
                string cmd = cmdObj?.ToString()?.ToLowerInvariant() ?? "";
                if (cmd.Contains("cpu") || cmd.Contains("processor"))
                    return new[] { "system_cpu_info", "system_cpu_usage", "system_report" };
                if (cmd.Contains("gpu") || cmd.Contains("nvidia") || cmd.Contains("vram"))
                    return new[] { "system_gpu_info", "system_gpu_usage", "system_gpu_processes" };
                if (cmd.Contains("memory") || cmd.Contains("ram") || cmd.Contains("freememory"))
                    return new[] { "system_memory", "system_report" };
                if (cmd.Contains("disk") || cmd.Contains("drive") || cmd.Contains("storage"))
                    return new[] { "system_disks", "system_report" };
                if (cmd.Contains("os") || cmd.Contains("systeminfo") || cmd.Contains("version"))
                    return new[] { "system_os", "system_software_report" };
                if (cmd.Contains("process") || cmd.Contains("tasklist"))
                    return new[] { "system_processes", "system_gpu_processes" };
                if (cmd.Contains("uptime") || cmd.Contains("lastbootuptime"))
                    return new[] { "system_uptime" };
            }
            return new[] { "system_report", "read_file", "search_files" };
        }

        if (lower == "read_file")
        {
            return new[] { "list_directory", "search_files", "run_command" };
        }

        if (lower == "edit_file" || lower == "replace_lines")
        {
            return new[] { "read_file", "write_file", "apply_patch" };
        }

        return new[] { "run_command", "read_file", "task_progress" };
    }

    public static IReadOnlyList<string> GetRecoverySteps(string toolName, string? failureClass)
    {
        var lower = toolName.ToLowerInvariant();

        if (lower == "list_directory")
        {
            return new[]
            {
                "change_arguments: Add 'filter' or 'offset' parameter to paginate or narrow search.",
                "use_search_files: Target specific file extensions (e.g. pattern='*.dll' or '*.log').",
                "use_read_file: If you already know the filename, read it directly.",
                "proceed_with_cached_data: Use the data returned in previous messages."
            };
        }

        if (lower == "run_command")
        {
            return new[]
            {
                "use_typed_tool: Prefer native 'system_*' tools (system_cpu_usage, system_gpu_info, system_memory, etc.) over brittle shell cmdlets.",
                "simplify_syntax: Ensure command is valid Windows PowerShell. Avoid Linux operators like '4>/dev/null' or 'head'.",
                "check_command_existence: Verify target cmdlet or executable exists on this machine."
            };
        }

        if (lower == "read_file")
        {
            return new[]
            {
                "verify_path: Check that the file path exists using 'list_directory' or 'search_files'.",
                "adjust_lines: Specify start_line and end_line for large files."
            };
        }

        return new[]
        {
            "change_arguments: Do not call the same tool with identical arguments.",
            "use_alternative_tool: Choose another tool capable of satisfying the objective.",
            "report_blocked: If no tool can satisfy the objective, report it blocked and move to next task."
        };
    }

    private static string GetGuidanceText(string toolName, int attemptCount, IReadOnlyList<string> alternatives)
    {
        string altList = alternatives.Count > 0 ? string.Join(", ", alternatives.Select(a => $"'{a}'")) : "alternative tools";
        return $"Execution of '{toolName}' was blocked after {attemptCount} identical attempts. Do NOT repeat the same action. Select one of the alternative tools: {altList}, or change your parameters.";
    }
}
