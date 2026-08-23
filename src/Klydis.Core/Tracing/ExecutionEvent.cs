using System;

namespace Klydis.Core.Tracing;

/// <summary>
/// Categories of execution events emitted along the canonical event stream.
/// </summary>
public enum ExecutionEventCategory
{
    TaskStarted,
    PlanCreated,
    PlanUpdated,
    StepStarted,
    ModelThinking,
    ToolProposed,
    ToolBlocked,
    ToolStarted,
    ToolOutput,
    ToolCompleted,
    FileRead,
    FileWritten,
    FileEdited,
    DiffCreated,
    ArtifactCreated,
    PreviewUpdated,
    VerificationStarted,
    VerificationPassed,
    VerificationFailed,
    RecoveryStarted,
    RecoveryCompleted,
    StepCompleted,
    TaskCompleted
}

/// <summary>
/// A canonical execution event representing an atomic lifecycle step in agent execution.
/// Monotonically sequenced and immutable for strict total ordering across all views.
/// </summary>
public sealed record ExecutionEvent
{
    /// <summary>Monotonic total sequence number (strictly increasing integer).</summary>
    public long SequenceNumber { get; init; }

    /// <summary>UTC timestamp of event generation.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Session identifier.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Task identifier if part of an autonomous task.</summary>
    public string? TaskId { get; init; }

    /// <summary>Step identifier if part of a task step.</summary>
    public string? StepId { get; init; }

    /// <summary>Action identifier if correlated with a specific tool invocation.</summary>
    public string? ActionId { get; init; }

    /// <summary>Loop iteration count.</summary>
    public int Iteration { get; init; }

    /// <summary>Category of this execution event.</summary>
    public ExecutionEventCategory Category { get; init; }

    /// <summary>Name of the tool involved (if any).</summary>
    public string? ToolName { get; init; }

    /// <summary>Status flag (e.g. true for success, false for failure/blocked).</summary>
    public bool Success { get; init; } = true;

    /// <summary>Semantic human-readable title (e.g. "Checking GPU telemetry", "Reading StepClassifier.cs").</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Short summary of action, outcome, or error.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Full text/JSON details or tool output preview.</summary>
    public string? Details { get; init; }

    /// <summary>Associated artifact identifier if applicable.</summary>
    public string? ArtifactId { get; init; }

    /// <summary>Associated filesystem path if applicable.</summary>
    public string? FilePath { get; init; }

    /// <summary>Duration of action execution in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Generates a semantic human-readable title from tool name and arguments.</summary>
    public static string GenerateSemanticTitle(string toolName, string? argsSummary = null)
    {
        string lower = (toolName ?? string.Empty).ToLowerInvariant();
        return lower switch
        {
            "system_cpu_info" or "system_cpu_usage" or "system_cpu_metrics" => "Checking CPU telemetry",
            "system_gpu_info" or "system_gpu_usage" or "system_gpu_metrics" or "system_gpu_processes" => "Checking GPU telemetry",
            "system_memory" or "system_memory_metrics" => "Inspecting system memory",
            "system_disks" or "system_disk_metrics" => "Inspecting storage disks",
            "system_os" or "system_os_info" => "Checking operating system info",
            "system_uptime" => "Checking system uptime",
            "system_temperatures" => "Checking thermal sensors",
            "system_processes" or "system_top_processes" or "process_find" => "Inspecting active processes",
            "system_report" or "system_hardware_report" or "system_software_report" => "Compiling system report",
            "read_file" => string.IsNullOrWhiteSpace(argsSummary) ? "Reading file" : $"Reading {argsSummary}",
            "write_file" => string.IsNullOrWhiteSpace(argsSummary) ? "Writing file" : $"Writing {argsSummary}",
            "edit_file" or "replace_lines" => string.IsNullOrWhiteSpace(argsSummary) ? "Editing file" : $"Editing {argsSummary}",
            "list_directory" => string.IsNullOrWhiteSpace(argsSummary) ? "Listing directory" : $"Listing {argsSummary}",
            "search_files" => string.IsNullOrWhiteSpace(argsSummary) ? "Searching files" : $"Searching for {argsSummary}",
            "run_command" => string.IsNullOrWhiteSpace(argsSummary) ? "Running shell command" : $"Running `{argsSummary}`",
            "search_web" => string.IsNullOrWhiteSpace(argsSummary) ? "Searching the web" : $"Searching web for '{argsSummary}'",
            "crawl_url" => string.IsNullOrWhiteSpace(argsSummary) ? "Crawling webpage" : $"Crawling {argsSummary}",
            "plan" => "Updating execution plan",
            "task_complete" => "Completing goal execution",
            _ => $"Executing {toolName}"
        };
    }
}
