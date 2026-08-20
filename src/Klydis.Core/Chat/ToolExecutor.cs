using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System.Management;
using Klydis.Core.Memory;
using Klydis.Core.Workbench;

namespace Klydis.Core.Chat;

/// <summary>
/// Represents a parameter for a tool.
/// </summary>
public record ToolParameter(string Name, string Type, string Description, bool Required, string[]? Enum = null);

/// <summary>
/// Defines the risk level mode for tool execution.
/// </summary>
public enum RiskLevel
{
    Safe,
    Standard,
    AutoPilot
}

/// <summary>
/// Represents a definition of an available tool.
/// </summary>
public record ToolDefinition(string Name, string Description, IList<ToolParameter> Parameters, bool RequiresApproval);

/// <summary>
/// Represents a request to call a tool.
/// </summary>
public record ToolCallRequest(string Name, IDictionary<string, object> Arguments);

/// <summary>
/// Represents the result of executing a tool.
/// </summary>
public record ToolResult(string ToolName, bool Success, string Output, string? Error, bool IsValidationError = false, int? ExitCode = null);

/// <summary>
/// A single recorded tool invocation for a session. Kept per session so the UI's right-side
/// panel can surface ONLY what a given chat actually did (files read/written, artifacts
/// produced, commands run) instead of workspace-global state.
/// </summary>
public sealed record ToolActivityRecord(string ToolName, string ArgsJson, bool Success, string OutputPreview, DateTime Timestamp);

/// <summary>
/// Event arguments for tool approval requests.
/// </summary>
public class ToolApprovalEventArgs : EventArgs
{
    public ToolCallRequest Request { get; }
    public bool IsApproved { get; set; }
    
    public ToolApprovalEventArgs(ToolCallRequest request)
    {
        Request = request;
    }
}

/// <summary>
/// Executes tools called by the model.
/// </summary>
public class ToolExecutor(
    ILogger<ToolExecutor> logger, 
    Klydis.Core.Memory.MessageStore messageStore, 
    Klydis.Core.Memory.ContextOrchestrator contextOrchestrator,
    ModelMessageQueue? messageQueue = null,
    Klydis.Core.Skills.SkillLibraryManager? skillLibraryManager = null,
    StealthBrowserService? stealthBrowserService = null,
    Klydis.Core.RAG.VectorStore? vectorStore = null,
    Klydis.Core.RAG.HybridRetriever? hybridRetriever = null,
    Klydis.Core.RAG.DocumentIngestionEngine? ingestionEngine = null,
    Klydis.Core.Learning.AdaptiveLearningService? adaptiveLearning = null,
    Klydis.Core.Tasks.TaskManager? taskManager = null)
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly StealthBrowserService? _stealthBrowserService = stealthBrowserService;

    /// <summary>
    /// The task currently executing, when the turn resolved one. Set by ChatEngine after
    /// task resolution and cleared when the turn ends. Drives (a) plan persistence, which
    /// mirrors to the task record so the checklist follows the task, and (b) queue reads,
    /// which are scoped to this task so the model never sees another task's queued items.
    /// Null outside a turn (direct invocations, tests) → legacy session-scoped behavior.
    /// </summary>
    public string? CurrentTaskId { get; set; }

    /// <summary>
    /// The active run id for <see cref="CurrentTaskId"/>, when the task layer resolved one.
    /// Durable activity/execution-event rows are stamped with it so tool activity is
    /// attributable to (task, run) — previously the durable rows were written with
    /// RunId: null, so a run's activity could not be reconstructed after a restart.
    /// Null outside a turn.
    /// </summary>
    public string? CurrentRunId { get; set; }

    /// <summary>
    /// The canonical task workspace root, when established. Used as the default working
    /// directory for run_command and as the base for tool-output offload, so autonomous
    /// execution stays inside the project boundary instead of the process working directory.
    /// Canonicalized (absolute path) on assignment; null = fall back to the process cwd
    /// (legacy behavior).
    /// </summary>
    private string? _workspaceRoot;
    public string? WorkspaceRoot
    {
        get => _workspaceRoot;
        set
        {
            try
            {
                _workspaceRoot = string.IsNullOrWhiteSpace(value) ? null : System.IO.Path.GetFullPath(value);
            }
            catch
            {
                _workspaceRoot = null;
            }
        }
    }

    public Klydis.Core.Tasks.TaskManager? TaskManager { get; set; } = taskManager;
    
    public ModelMessageQueue? MessageQueue { get; set; } = messageQueue;
    public Klydis.Core.Skills.SkillLibraryManager? SkillLibraryManager { get; set; } = skillLibraryManager;
    public Klydis.Core.RAG.VectorStore? VectorStore { get; set; } = vectorStore;
    public Klydis.Core.RAG.HybridRetriever? HybridRetriever { get; set; } = hybridRetriever;
    public Klydis.Core.RAG.DocumentIngestionEngine? IngestionEngine { get; set; } = ingestionEngine;
    public Klydis.Core.Learning.AdaptiveLearningService? AdaptiveLearning { get; set; } = adaptiveLearning;

    /// <summary>
    /// Gets or sets the current risk level mode.
    /// </summary>
    public RiskLevel CurrentRiskLevel { get; set; } = RiskLevel.Standard;

    /// <summary>
    /// Generic per-tool-call wall-clock timeout applied at dispatch, on top of any timeout a
    /// tool already implements internally (crawl, browser, command execution). Guards the
    /// whole turn against a single hung tool call — the task-level MaxTurnDuration can only
    /// stop the loop BETWEEN iterations, so a stalled tool would otherwise block everything.
    /// A timeout returns a failed ToolResult with guidance; the identical-failure circuit
    /// breaker then blocks repeated retries of the same hung call.
    /// <summary>
    /// Default timeout for individual tool calls. Extended to 1 hour to support heavy computations and large models.
    /// </summary>
    public TimeSpan ToolCallTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Per-tool timeouts for operations that legitimately run longer than the default
    /// (network crawling, compilation, LLM-driven indexing/summarization).
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, TimeSpan> ToolCallTimeoutOverrides =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            // run_command is deliberately absent: it already self-bounds via its own
            // timeout_seconds argument (default 120s), so the dispatch-level budget only
            // needs to backstop tools without an internal timeout.
            ["crawl_url"] = TimeSpan.FromHours(1),
            ["search_web"] = TimeSpan.FromMinutes(30),
            ["store_memory"] = TimeSpan.FromMinutes(30),
            ["summarize_context"] = TimeSpan.FromHours(1),
            ["index_folder_rag"] = TimeSpan.FromHours(2),
            ["retrieve_memory"] = TimeSpan.FromMinutes(30)
        };

    // Custom tool definitions are re-queried from SQLite on EVERY tool execution and prompt
    // build (2+ DB roundtrips per tool call in the generation loop). Cache them and invalidate
    // on create/delete — ToolExecutor is a singleton, so the cache is safe.
    private List<ToolDefinition>? _customToolDefinitionsCache;
    private readonly object _customToolsCacheLock = new();

    /// <summary>
    /// Gets or sets whether automatic tool output offloading to disk is enabled for large results.
    /// </summary>
    public bool EnableOutputOffloading { get; set; } = true;

    /// <summary>
    /// Maximum character threshold (~3000 tokens) before tool output is offloaded to disk.
    /// </summary>
    public int MaxToolOutputChars { get; set; } = 12000;

    /// <summary>
    /// Number of preview characters retained in prompt when tool output is offloaded.
    /// </summary>
    public int OffloadPreviewChars { get; set; } = 1500;

    private readonly IList<ToolDefinition> _tools = new List<ToolDefinition>
    {
        new ToolDefinition("read_file", "Reads content from a file. Optionally specify start_line and end_line for specific line ranges of large files.", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the file", true),
            new("start_line", "integer", "Optional starting line number (1-indexed)", false),
            new("end_line", "integer", "Optional ending line number (1-indexed)", false)
        }, false),
        new ToolDefinition("write_file", "Writes content to a file", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the file", true),
            new("content", "string", "Content to write", true)
        }, false),
        new ToolDefinition("edit_file", "Applies a targeted text replacement inside an existing file. Use for incremental edits: provide the old_text block to replace and the new_text replacement. The old_text is matched exactly, falling back to a whitespace/indentation-tolerant match (so indentation or line-ending drift does not break the edit). The match must appear exactly once — if it matches zero or multiple places, the call fails with guidance. The harness captures a real diff and registers the artifact exactly like write_file.", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the file", true),
            new("old_text", "string", "The text to replace (matched exactly, then whitespace/indentation-tolerantly; must be unique)", true),
            new("new_text", "string", "The replacement text", true)
        }, false),
        new ToolDefinition("apply_patch", "Applies a standard unified diff (diff -u format with ---/+++ headers and @@ hunks) to an existing file. Use for multi-hunk edits or when the change is easier to express as a patch. Hunks are applied in order with trailing-whitespace and line-ending tolerance. The harness captures a real diff and registers the artifact exactly like edit_file.", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the file", true),
            new("patch", "string", "The unified diff (diff -u format: --- / +++ headers and @@ -a,b +c,d @@ hunks)", true)
        }, false),
        new ToolDefinition("list_directory", "Lists immediate children of a directory with sizes. For very large directories (e.g. system32), consider using search_files instead.", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the directory", true)
        }, false),
        new ToolDefinition("run_command", "Executes a PowerShell command and returns stdout/stderr. Only use real PowerShell cmdlets. For app launching use Start-Process -FilePath ... -ArgumentList ... Pipe large outputs through Select-Object -First N.", new List<ToolParameter>
        {
            new("command", "string", "Command to execute", true),
            new("working_directory", "string", "Optional working directory for command execution", false),
            new("timeout_seconds", "integer", "Optional timeout in seconds (default 60)", false)
        }, false),
        new ToolDefinition("get_system_info", "Returns CPU, RAM, GPU, and disk info", new List<ToolParameter>(), false),
        new ToolDefinition("search_web", "Searches the web and returns top 5 results with clean target URLs, titles, and snippets. Summarize results for the user rather than dumping raw output.", new List<ToolParameter>
        {
            new("query", "string", "Search query", true),
            new("max_results", "integer", "Optional maximum results to return (default 5)", false)
        }, false),
        new ToolDefinition("crawl_url", "Fetches and renders a web page, extracts main content as clean Markdown. Use for reading specific documentation pages or articles. Tries a fast direct HTTP fetch first (no browser needed); falls back to a stealth browser for JavaScript-heavy pages.", new List<ToolParameter>
        {
            new("url", "string", "Target URL", true)
        }, false),
        new ToolDefinition("search_files", "Searches directory recursively for files matching a pattern. Returns up to 20 matches. Do NOT call repeatedly with identical arguments.", new List<ToolParameter>
        {
            new("path", "string", "Directory to search", true),
            new("pattern", "string", "File pattern (e.g. *.cs)", true),
            new("contains", "string", "Optional text to search inside files", false)
        }, false),
        new ToolDefinition("store_memory", "Saves an important fact into the persistent session world state", new List<ToolParameter>
        {
            new("fact", "string", "The fact or information to remember", true)
        }, false),
        new ToolDefinition("retrieve_memory", "Searches past chat history for context", new List<ToolParameter>
        {
            new("query", "string", "Search query", true)
        }, false),
        new ToolDefinition("summarize_context", "Compresses older messages into the world state to free up context window", new List<ToolParameter>(), false),
        new ToolDefinition("check_message_queue", "Checks pending user messages waiting in the processing queue for the active session. Call this periodically during multi-step tasks to check for user context updates or steering instructions.", new List<ToolParameter>(), false),
        new ToolDefinition("incorporate_queued_message", "Retrieves and incorporates a pending queued user message by queue_id to steer the current reasoning or execution task.", new List<ToolParameter>
        {
            new("queue_id", "string", "The ID (Guid string) of the queued message to incorporate", true)
        }, false),
        new ToolDefinition("create_custom_tool", "Creates a new custom tool. The schema defines parameters, and the script uses them.", new List<ToolParameter>
        {
            new("name", "string", "Tool name (no spaces)", true),
            new("description", "string", "What the tool does", true),
            new("language", "string", "Language for the script: 'powershell', 'python', or 'csharp'", true),
            new("parameters_schema", "string", "JSON array of ToolParameter objects: [{\"Name\": \"arg1\", \"Type\": \"string\", \"Description\": \"...\", \"Required\": true}]", true),
            new("script_content", "string", "Script content. Access args via env vars ($env:arg1 in PS, os.environ['arg1'] in Python, Environment.GetEnvironmentVariable(\"arg1\") in C#)", true)
        }, false),
        new ToolDefinition("delete_custom_tool", "Deletes a custom tool.", new List<ToolParameter>
        {
            new("name", "string", "Tool name to delete", true)
        }, false),
        new ToolDefinition("list_skills", "Lists skills in the Brain Skill Library, optionally filtered by category.", new List<ToolParameter>
        {
            new("category", "string", "Optional category filter", false)
        }, false),
        new ToolDefinition("search_skills", "Searches the Brain Skill Library for skills matching a keyword query.", new List<ToolParameter>
        {
            new("query", "string", "Search query or keyword", true)
        }, false),
        new ToolDefinition("get_skill_details", "Retrieves full details and prompt directives of a specific skill by skill_id.", new List<ToolParameter>
        {
            new("skill_id", "string", "The ID of the skill (e.g. 'mcp-builder')", true)
        }, false),
        new ToolDefinition("activate_skill", "Activates and retrieves the full prompt directives of a skill for immediate task execution.", new List<ToolParameter>
        {
            new("skill_id", "string", "The ID of the skill to activate", true)
        }, false),
        new ToolDefinition("learn_skill", "Creates and persists a new custom skill into the Brain Skill Library for future use.", new List<ToolParameter>
        {
            new("name", "string", "Skill name (e.g. 'WPF MVVM Expert')", true),
            new("description", "string", "Brief description of what the skill provides", true),
            new("category", "string", "Skill category (e.g. 'Development', 'AI & ML Infrastructure', 'Creative')", true),
            new("prompt_instruction", "string", "Detailed prompt directives and instructions for the skill", true),
            new("tags", "string", "Comma-separated tags (e.g. 'wpf, csharp, mvvm')", false)
        }, false),
        new ToolDefinition("delete_skill", "Deletes a custom skill from the Brain Skill Library by ID.", new List<ToolParameter>
        {
            new("skill_id", "string", "The ID of the custom skill to delete", true)
        }, false),
        new ToolDefinition("search_rag", "Searches indexed project vector stores and document collections using hybrid (dense vector + sparse BM25) search. Returns matching text chunks with source file paths and relevance scores.", new List<ToolParameter>
        {
            new("query", "string", "Search query or keyword", true),
            new("top_k", "integer", "Optional maximum number of results to return (default 5)", false),
            new("collection_id", "string", "Optional collection ID filter", false)
        }, false),
        new ToolDefinition("list_rag_collections", "Lists all currently indexed workspace folders and project document collections in the RAG vector store.", new List<ToolParameter>(), false),
        new ToolDefinition("index_folder_rag", "Indexes a local project folder or directory path into the RAG vector store so its contents can be searched via search_rag.", new List<ToolParameter>
        {
            new("folder_path", "string", "Absolute directory path of the project or folder to index", true),
            new("collection_name", "string", "Optional custom collection name (defaults to folder name)", false)
        }, false),
        new ToolDefinition("learn_lesson", "Persists a lesson learned during this task into the model's cross-session learning store. Use when you discovered something important: a workflow that worked, a tool behavior quirk, a useful approach, or a mistake to avoid. Future sessions with this model will see these lessons.", new List<ToolParameter>
        {
            new("lesson", "string", "The lesson content: what was learned and why it matters", true),
            new("category", "string", "Optional category, e.g. 'workflow', 'tool-behavior', 'pitfall' (default 'general')", false)
        }, false),
        new ToolDefinition("recall_lessons", "Retrieves lessons previously recorded for this model (from this or past sessions) so you can apply accumulated knowledge to the current task.", new List<ToolParameter>
        {
            new("limit", "integer", "Optional maximum number of lessons to return (default 8)", false)
        }, false),
        new ToolDefinition("task_complete", "Signals that the current user goal or multi-step task has been fully completed. Call this ONLY when the goal is 100% accomplished. Provide a clear, detailed summary of what was completed.", new List<ToolParameter>
        {
            new("summary", "string", "Summary of what was accomplished to complete the goal", true)
        }, false),
        new ToolDefinition("task_progress", "Reports intermediate progress toward the current goal during autonomous multi-turn execution.", new List<ToolParameter>
        {
            new("percent", "integer", "Estimated percentage of goal completion (0-100)", true),
            new("status", "string", "Brief description of current progress and next steps", true)
        }, false),
        new ToolDefinition("plan", "Maintains the agent's todo list for the current task or goal. The plan is persisted for this chat and re-injected into your context every turn, so it is the authoritative checklist you keep updating. Use action 'create' with a newline-separated 'items' list to establish the plan, 'add' to append tasks, 'complete' to mark a task done (match by its number or text), 'remove' to delete a task, 'show' to review the current plan, and 'clear' to reset it.", new List<ToolParameter>
        {
            new("action", "string", "One of: create, add, complete, remove, show, clear (defaults to 'show' if omitted, 'create' if items provided, or 'complete' if item provided)", false, new[] { "create", "add", "complete", "remove", "show", "clear" }),
            new("items", "string", "Newline-separated list of tasks (for action=create or add)", false),
            new("item", "string", "Single task to complete or remove — match by its number (e.g. '2') or by text", false),
            new("progress", "integer", "Optional overall completion percent 0-100", false)
        }, false)
    };

    /// <summary>
    /// Optional asynchronous handler for tool approval. If set, this delegate will be awaited instead of synchronously invoking ToolApprovalRequested.
    /// </summary>
    public Func<ToolCallRequest, Task<bool>>? ToolApprovalHandlerAsync { get; set; }

    /// <summary>
    /// Event triggered when a tool requires user approval.
    /// </summary>
    public event EventHandler<ToolApprovalEventArgs>? ToolApprovalRequested;

    /// <summary>
    /// Event triggered when a tool has been executed.
    /// </summary>
    public event EventHandler<ToolResult>? ToolExecuted;

    /// <summary>
    /// Gets all tool definitions, combining built-in tools with custom tools from the database.
    /// </summary>
    public async Task<IList<ToolDefinition>> GetToolDefinitionsAsync()
    {
        var allTools = new List<ToolDefinition>(_tools);
        if (messageStore == null) return allTools;

        lock (_customToolsCacheLock)
        {
            if (_customToolDefinitionsCache != null)
            {
                allTools.AddRange(_customToolDefinitionsCache);
                return allTools;
            }
        }

        var customTools = await messageStore.GetCustomToolsAsync();
        var customDefinitions = new List<ToolDefinition>(customTools.Count);

        foreach (var ct in customTools)
        {
            var parameters = new List<ToolParameter>();
            try
            {
                if (!string.IsNullOrWhiteSpace(ct.ParametersJson))
                {
                    parameters = JsonSerializer.Deserialize<List<ToolParameter>>(ct.ParametersJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ToolParameter>();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse parameters for custom tool {ToolName}", ct.Name);
            }

            // Custom tools are now allowed by default, subject to risky request detection
            customDefinitions.Add(new ToolDefinition(ct.Name, ct.Description, parameters, RequiresApproval: false));
        }

        lock (_customToolsCacheLock)
        {
            _customToolDefinitionsCache ??= customDefinitions;
            allTools.AddRange(_customToolDefinitionsCache);
        }

        return allTools;
    }

    private void InvalidateCustomToolsCache()
    {
        lock (_customToolsCacheLock)
        {
            _customToolDefinitionsCache = null;
        }
    }

    /// <summary>
    /// Formats tools as a JSON schema for the prompt.
    /// </summary>
    public string FormatToolsForPrompt(IList<ToolDefinition> tools)
    {
        var schema = tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = new
                {
                    type = "object",
                    properties = t.Parameters.ToDictionary(
                        p => p.Name,
                        p => new { type = p.Type, description = p.Description, @enum = p.Enum }
                    ),
                    required = t.Parameters.Where(p => p.Required).Select(p => p.Name).ToArray()
                }
            }
        });

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Executes a tool request asynchronously.
    /// </summary>
    public async Task<ToolResult> ExecuteToolAsync(ToolCallRequest request, string sessionId, CancellationToken ct, string? modelPath = null)
    {
        logger.LogInformation("Executing tool: {ToolName}", request.Name);

        // Propagate cancellation immediately instead of letting the per-tool catch swallow it
        // and feeding a bogus "error" result back into the generation loop.
        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        // Serialize the arguments once — shared by duplicate-call tracking and the session
        // activity record.
        string argsJson = string.Empty;
        if (request.Arguments != null)
        {
            try { argsJson = System.Text.Json.JsonSerializer.Serialize(request.Arguments); } catch { /* best effort */ }
        }
        var tools = await GetToolDefinitionsAsync();
        // P1: the gate compares tool names case-insensitively; the executor must resolve the
        // same way. A case-sensitive lookup let calls like READ_FILE pass the gate and then
        // fail dispatch as "unknown tool".
        var toolDef = tools.FirstOrDefault(t => t.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
        
        if (toolDef == null)
        {
            var validToolNames = string.Join(", ", tools.Select(t => t.Name));
            string commandHint = (request.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("terminal", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("exec", StringComparison.OrdinalIgnoreCase))
                ? $"\nAction Required: You attempted to call '{request.Name}' as a tool name. To run command line commands or launch processes, call tool 'run_command' with argument {{\"command\": \"...\"}}."
                : "";

            var guidance = $"Tool '{request.Name}' does not exist in available system tools.{commandHint}\n" +
                           $"Available valid tools are: [{validToolNames}].\n" +
                           $"Guidance: Use 'run_command' for system commands, 'read_file'/'write_file' for file operations, or 'search_web'/'search_rag' for retrieval.";
            return new ToolResult(request.Name, false, string.Empty, guidance);
        }

        // P1: normalize the tool name ONCE, immediately after resolution. The gate validates
        // case-insensitively (READ_FILE passes), so the identical-retry key, the dispatch
        // switch, the activity/durable rows, and the tool result must all use ONE canonical
        // name — otherwise a mixed-case call falls through the switch into custom-tool
        // dispatch and fails as an unknown custom tool.
        string canonicalName = toolDef.Name;
        string callKey = $"{canonicalName}|{argsJson}";

        // Refuse an identical-failed-call retry loop BEFORE it wastes another turn: once a
        // call has failed 3+ consecutive times with the same arguments, block it outright.
        if (CheckIdenticalRetry(sessionId, callKey, out string blockMessage))
        {
            TrackIdenticalCallOutcome(sessionId, callKey, succeeded: false);
            return await FinishToolCallAsync(request, sessionId, argsJson,
                new ToolResult(canonicalName, false, string.Empty, blockMessage), canonicalName);
        }

        bool isRisky = IsRiskyRequest(request);
        bool requiresApproval = false;

        if (CurrentRiskLevel == RiskLevel.Safe)
        {
            requiresApproval = true;
        }
        else if (CurrentRiskLevel == RiskLevel.Standard)
        {
            requiresApproval = toolDef.RequiresApproval || isRisky;
        }
        else // AutoPilot
        {
            requiresApproval = false;
        }

        if (requiresApproval)
        {
            bool isApproved = false;
            if (ToolApprovalHandlerAsync != null)
            {
                isApproved = await ToolApprovalHandlerAsync(request);
            }
            else
            {
                var args = new ToolApprovalEventArgs(request);
                ToolApprovalRequested?.Invoke(this, args);
                isApproved = args.IsApproved;
            }

            if (!isApproved)
            {
                var denyReason = isRisky && CurrentRiskLevel == RiskLevel.Standard 
                    ? "Tool execution denied automatically due to potentially risky content." 
                    : "Tool execution denied by user.";
                return new ToolResult(request.Name, false, string.Empty, denyReason);
            }
        }

        // Per-tool-call timeout: a single hung tool must never block the whole turn (the
        // task-level MaxTurnDuration only fires between iterations). Linked to the caller's
        // token so user cancellation still wins over the timeout.
        TimeSpan effectiveToolTimeout = ToolCallTimeoutOverrides.TryGetValue(request.Name, out var toolOverride)
            ? toolOverride
            : ToolCallTimeout;
        using var toolTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        toolTimeoutCts.CancelAfter(effectiveToolTimeout);
        var toolCt = toolTimeoutCts.Token;

        ToolResult result;
        try
        {
            result = canonicalName switch
            {
                "read_file" => await ReadFileAsync(request, toolCt),
                "write_file" => await WriteFileAsync(request, sessionId, toolCt),
                "edit_file" => await EditFileAsync(request, sessionId, toolCt),
                "apply_patch" => await ApplyPatchAsync(request, sessionId, toolCt),
                "list_directory" => await ListDirectoryAsync(request, toolCt),
                "run_command" => await RunCommandAsync(request, toolCt),
                "get_system_info" => await GetSystemInfoAsync(toolCt),
                "search_web" => await SearchWebAsync(request, toolCt),
                "crawl_url" => await CrawlUrlAsync(request, toolCt),
                "search_files" => await SearchFilesAsync(request, toolCt),
                "store_memory" => await StoreMemoryAsync(request, sessionId, toolCt),
                "retrieve_memory" => await RetrieveMemoryAsync(request, sessionId, toolCt),
                "summarize_context" => await SummarizeContextAsync(request, sessionId, toolCt),
                "check_message_queue" => await CheckMessageQueueAsync(sessionId),
                "incorporate_queued_message" => await IncorporateQueuedMessageAsync(request, sessionId),
                "create_custom_tool" => await CreateCustomToolAsync(request, toolCt),
                "delete_custom_tool" => await DeleteCustomToolAsync(request, toolCt),
                "list_skills" => await ListSkillsAsync(request),
                "search_skills" => await SearchSkillsAsync(request),
                "get_skill_details" => await GetSkillDetailsAsync(request),
                "activate_skill" => await ActivateSkillAsync(request),
                "learn_skill" => await LearnSkillAsync(request),
                "delete_skill" => await DeleteSkillAsync(request),
                "search_rag" => await SearchRagAsync(request, toolCt),
                "list_rag_collections" => await ListRagCollectionsAsync(toolCt),
                "index_folder_rag" => await IndexFolderRagAsync(request, toolCt),
                "learn_lesson" => await LearnLessonAsync(request, toolCt, modelPath),
                "recall_lessons" => await RecallLessonsAsync(request, toolCt, modelPath),
                "task_complete" => ExecuteTaskComplete(request),
                "task_progress" => ExecuteTaskProgress(request),
                "plan" => await ExecutePlanAsync(request, sessionId),
                _ => await ExecuteCustomToolAsync(request, toolCt)
            };
        }
        catch (OperationCanceledException) when (toolTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The per-call budget fired (not the user cancelling): surface an actionable
            // timeout instead of a generic cancellation. The identical-failure circuit
            // breaker will block a model that keeps retrying the same hung call.
            logger.LogWarning("Tool '{ToolName}' exceeded the per-call timeout of {Timeout} and was cancelled.", request.Name, effectiveToolTimeout);
            result = new ToolResult(request.Name, false, string.Empty,
                $"⚠ Tool call '{request.Name}' exceeded the per-call timeout of {(int)effectiveToolTimeout.TotalSeconds} s and was cancelled. " +
                $"Re-plan: split the operation into smaller steps or reduce its scope before retrying.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing tool {ToolName}", request.Name);
            result = new ToolResult(request.Name, false, string.Empty, ex.Message);
        }

        // Escalate identical-failed-call retry loops: the 2nd consecutive identical failure
        // gets a warning appended, the 3rd+ gets an explicit BLOCKED message, and any further
        // identical call is refused BEFORE it executes (see the pre-dispatch check above).
        var (_, postMessage) = TrackIdenticalCallOutcome(sessionId, callKey, result.Success);
        if (!string.IsNullOrWhiteSpace(postMessage))
        {
            result = result with
            {
                Output = string.IsNullOrEmpty(result.Output) ? postMessage : result.Output + $"\n\n{postMessage}"
            };
        }

        // Canonicalize the result's tool name so every consumer (activity rows, durable
        // store, execution events, and the model's next prompt) sees the registered name.
        result = result with { ToolName = canonicalName };
        return await FinishToolCallAsync(request, sessionId, argsJson, result, canonicalName);
    }

    private async Task<ToolResult> FinishToolCallAsync(ToolCallRequest request, string sessionId, string argsJson, ToolResult result, string canonicalName)
    {
        // Hydrate first so the in-memory cache is the DB contents before this invocation is
        // appended — otherwise a tool call that lands before the UI's first read would be
        // recorded in memory, then re-appended by the lazy hydration, duplicating it.
        EnsureSessionToolActivityLoaded(sessionId);

        // Record the invocation for the right-side panel (session-scoped). Args are serialized
        // so path-bearing tools can be surfaced as "files this chat worked with", and the
        // output preview is sized for the Terminal tab's bracketed command/output transcript
        // (a 220-char stub made run_command results useless to read).
        string outputPreview = string.Empty;
        try
        {
            if (!string.IsNullOrEmpty(result.Output))
            {
                outputPreview = result.Output.Length > 6000 ? result.Output.Substring(0, 6000) : result.Output;
            }
            var activityList = _sessionToolActivity.GetOrAdd(sessionId ?? string.Empty, _ => new List<ToolActivityRecord>());
            // P1: the per-session activity list is mutated from tool-completion threads while
            // the UI polls it every 2s — guard the mutation so a torn read can never surface.
            lock (_toolActivityLock)
            {
                activityList.Add(new ToolActivityRecord(canonicalName, argsJson, result.Success, outputPreview, DateTime.Now));
                // Bound the per-session activity history so long autonomous runs cannot grow it
                // without limit (the panel only renders the most recent commands anyway).
                if (activityList.Count > 500)
                {
                    activityList.RemoveRange(0, activityList.Count - 500);
                }
            }
        }
        catch { /* recording must never break tool execution */ }

        // Durable: persist to SQLite so Files/Preview/Terminal survive restarts and model
        // switches. The in-memory list above is a cache of tool_activity, not the authority.
        try
        {
            await messageStore.AddToolActivityAsync(new ToolActivityRow(
                ActivityId: Guid.NewGuid().ToString("N"),
                SessionId: sessionId ?? string.Empty,
                TaskId: CurrentTaskId,
                RunId: CurrentRunId,
                ToolName: canonicalName,
                ArgsJson: argsJson,
                Success: result.Success,
                OutputPreview: outputPreview,
                TimestampUtc: DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist tool activity for {ToolName}.", canonicalName);
        }

        // Durable execution event for the tool lifecycle.
        try
        {
            await EmitExecutionEventAsync(sessionId, result.Success ? "ToolCompleted" : "ToolFailed", CurrentTaskId, canonicalName, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit tool event for {ToolName}.", canonicalName);
        }

        result = ProcessToolOutputOffload(result);
        ToolExecuted?.Invoke(this, result);
        return result;
    }

    private ToolResult ProcessToolOutputOffload(ToolResult result)
    {
        // Paths that never reach the offload branch (failed tools with giant error blobs,
        // empty output, or offloading disabled): hard-truncate in place so a runaway command's
        // output cannot blow up the context window or the chat UI.
        if (!EnableOutputOffloading || !result.Success || string.IsNullOrEmpty(result.Output))
        {
            if (result.Output.Length > MaxToolOutputChars)
            {
                return result with
                {
                    Output = result.Output[..MaxToolOutputChars] + $"\n...[truncated: {result.Output.Length - MaxToolOutputChars} more chars]"
                };
            }
            return result;
        }

        if (result.Output.Length <= MaxToolOutputChars)
            return result;

        // Prevent recursive offloading loops when reading an offloaded tool output file or when content is already an offload message
        if (result.Output.StartsWith("[Tool Output Exceeded Context Budget]"))
            return result;

        try
        {
            // P0: offload output under the task workspace root when one is established, so
            // internal tool output never escapes the project boundary and the model can reach
            // it with workspace-scoped file tools. Falls back to the process cwd (legacy).
            var offloadRoot = WorkspaceRoot ?? Directory.GetCurrentDirectory();
            var offloadDir = Path.Combine(offloadRoot, ".klydis", "artifacts", "tool_outputs");
            Directory.CreateDirectory(offloadDir);

            var fileName = $"offload_{result.ToolName}_{Guid.NewGuid():N}.txt";
            var filePath = Path.Combine(offloadDir, fileName);

            File.WriteAllText(filePath, result.Output);

            // Count lines cheaply (streaming) so the directive can teach pagination: reading
            // the whole file back in one read_file call re-offloads and loops forever.
            int lineCount = 0;
            try
            {
                using var reader = new StreamReader(filePath, Encoding.UTF8);
                while (reader.ReadLine() != null) lineCount++;
            }
            catch { /* best effort */ }

            var preview = result.Output.Length > OffloadPreviewChars 
                ? result.Output[..OffloadPreviewChars] 
                : result.Output;

            // M1: Directive language — model MUST read the file before responding
            var offloadedMessage = $"[Tool Output Exceeded Context Budget]\n" +
                                   $"Full output ({result.Output.Length} characters) offloaded to: {filePath}\n\n" +
                                   $"Preview (First {preview.Length} characters):\n" +
                                   $"--------------------------------------------------\n" +
                                   $"{preview}\n" +
                                   $"--------------------------------------------------\n" +
                                   $"[ACTION REQUIRED: You MUST call tool read_file with path '{filePath}' to retrieve the complete content before forming your response. Do NOT tell the user to read the file themselves — read it now and synthesize the answer. " +
                                   $"(The file has {lineCount} line(s): read it in RANGES with start_line and end_line, about 100 lines per call, so the content stays in your context — do NOT re-read the whole file in one call.)]";

            logger.LogInformation("Tool output for {ToolName} ({CharCount} chars) offloaded to {FilePath}", result.ToolName, result.Output.Length, filePath);

            return new ToolResult(result.ToolName, result.Success, offloadedMessage, result.Error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to offload tool output to disk for {ToolName}", result.ToolName);
            // Offload failed: fall back to in-place truncation so the context stays bounded.
            if (result.Output.Length > MaxToolOutputChars)
            {
                return result with
                {
                    Output = result.Output[..MaxToolOutputChars] + $"\n...[truncated: {result.Output.Length - MaxToolOutputChars} more chars]"
                };
            }
            return result;
        }
    }

    /// <summary>
    /// Models frequently wrap their command in `powershell -Command "..."` because they don't
    /// realize run_command ALREADY executes PowerShell. Re-wrapping is destructive: the outer
    /// PowerShell parses the quoted string and interpolates $variables away (observed:
    /// `powershell -Command "$lines = ..."` ran as `= ...` with an empty $lines). Detect the
    /// wrapper and unwrap it so the intended script executes verbatim.
    /// </summary>
    public static string NormalizePowershellWrapper(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return command;
        var match = Regex.Match(command, @"^\s*(?:powershell|pwsh)(?:\.exe)?(?:\s+-[A-Za-z]+)*\s*(?:-Command|-c)\s+([""'])(?<script>.*)\1\s*$", RegexOptions.IgnoreCase);
        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups["script"].Value))
        {
            return match.Groups["script"].Value.Trim();
        }
        return command;
    }

    public static object? UnwrapJsonElement(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => element.GetRawText()
            };
        }
        return value;
    }

    public static string? GetStringArg(IDictionary<string, object>? args, string key)
    {
        if (args == null || !args.TryGetValue(key, out var val) || val == null)
            return null;
        var unwrapped = UnwrapJsonElement(val);
        return unwrapped?.ToString();
    }

    /// <summary>
    /// True when a 'path' argument contains shell command syntax instead of being a plain
    /// filesystem path. Models sometimes concatenate cmd/PowerShell into a path argument
    /// (observed: `C:\... 2>nul && dir /b ... | findstr` passed to list_directory); those
    /// calls can never succeed and the repeated attempts waste turns — fail fast instead.
    /// </summary>
    public static bool IsPathArgCommandLike(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.IndexOf("&&", StringComparison.Ordinal) >= 0
            || path.IndexOf("||", StringComparison.Ordinal) >= 0
            || path.IndexOf('|') >= 0
            || path.IndexOf('<') >= 0
            || path.IndexOf('>') >= 0
            || path.IndexOf('`') >= 0
            || path.IndexOf("2>nul", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("2>&1", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf('\r') >= 0
            || path.IndexOf('\n') >= 0;
    }

    /// <summary>
    /// Returns a clear failure result when a path argument looks like shell syntax, so the
    /// model learns to pass a single plain path (or use run_command) instead of retrying.
    /// Returns null when the path is fine.
    /// </summary>
    private static ToolResult? CommandLikePathResult(ToolCallRequest request, string path)
    {
        if (!IsPathArgCommandLike(path)) return null;
        string shown = path.Replace("\r", " ").Replace("\n", " ");
        if (shown.Length > 140) shown = shown.Substring(0, 140) + "…";
        return new ToolResult(request.Name, false, "",
            $"Invalid 'path' argument for '{request.Name}': it looks like shell command syntax, not a filesystem path (\"{shown}\"). " +
            $"The 'path' parameter takes ONE plain filesystem path. If you meant to run a command, use run_command instead — do NOT embed shell syntax in a path.");
    }

    /// <summary>
    /// True when a request would EXECUTE potentially destructive operations. Risk detection is
    /// deliberately scoped to execution-capable tools (run_command, create_custom_tool,
    /// delete_custom_tool) — scanning every argument of every tool produced false positives that
    /// blocked legitimate use (e.g. a search_web query containing "curl" or a write_file whose
    /// content mentions "Remove-Item" got flagged and denied). Content that is only read,
    /// written, or searched is never risky by itself.
    /// </summary>
    private bool IsRiskyRequest(ToolCallRequest request)
    {
        // Only tools that spawn processes or persist executable scripts can be risky.
        // Compared case-insensitively: the gate normalizes names, so RUN_COMMAND must be
        // treated exactly like run_command here or the approval policy silently diverges.
        if (!request.Name.Equals("run_command", StringComparison.OrdinalIgnoreCase) &&
            !request.Name.Equals("create_custom_tool", StringComparison.OrdinalIgnoreCase) &&
            !request.Name.Equals("delete_custom_tool", StringComparison.OrdinalIgnoreCase))
            return false;

        var contentToCheck = "";
        if (request.Arguments != null)
        {
            foreach (var arg in request.Arguments)
            {
                contentToCheck += UnwrapJsonElement(arg.Value)?.ToString() + " ";
            }
        }

        var riskyKeywords = new[] { 
            "rm -rf", "Remove-Item", "del ", "format ", "diskpart", 
            "Set-ExecutionPolicy", "Invoke-WebRequest", "wget ", "curl ", 
            "netsh ", "reg add", "reg delete", "Drop Table", "Drop Database" 
        };

        foreach (var keyword in riskyKeywords)
        {
            if (contentToCheck.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds an INVALID_CALL result for a structurally malformed tool request (missing required
    /// argument, invalid path shape, etc.). ChatEngine routes these into the escalating
    /// parse-failure path instead of treating them as ordinary tool results — so a model that
    /// repeatedly opens calls with empty arguments gets the "fix the call" feedback and,
    /// eventually, the suspend-and-answer exit ramp (the observed "Command is required" ×7
    /// loop). Zero-arg tools (get_system_info, list_rag_collections, ...) are unaffected: they
    /// have no required parameters.
    /// </summary>
    private static ToolResult InvalidCall(string toolName, string message)
    {
        return new ToolResult(toolName, false, string.Empty, message, IsValidationError: true);
    }

    private async Task<ToolResult> ReadFileAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        if (string.IsNullOrEmpty(path)) return InvalidCall(request.Name, "Path is required");
        var commandLike = CommandLikePathResult(request, path);
        if (commandLike != null) return commandLike;
        if (!File.Exists(path)) return new ToolResult(request.Name, false, "", "File not found");

        int? startLine = null;
        int? endLine = null;
        if (request.Arguments != null && request.Arguments.TryGetValue("start_line", out var slObj))
        {
            var unwrapped = UnwrapJsonElement(slObj);
            if (unwrapped != null && int.TryParse(unwrapped.ToString(), out int sl) && sl > 0)
                startLine = sl;
        }
        if (request.Arguments != null && request.Arguments.TryGetValue("end_line", out var elObj))
        {
            var unwrapped = UnwrapJsonElement(elObj);
            if (unwrapped != null && int.TryParse(unwrapped.ToString(), out int el) && el > 0)
                endLine = el;
        }

        if (startLine.HasValue || endLine.HasValue || path.Contains("offload_") || path.Contains("tool_outputs"))
        {
            // Stream lines lazily instead of materializing the whole file: offloaded tool
            // outputs can be tens of MB, and ReadAllLinesAsync would spike memory to hold them.
            int start = Math.Max((startLine ?? 1) - 1, 0);
            int count = int.MaxValue;
            if (endLine.HasValue)
            {
                // Both start_line/end_line are 1-based and inclusive: deliver exactly
                // end_line - start_line + 1 lines (start is 0-based internally, so use the
                // original 1-based value here — mixing them over-delivers by one line).
                int end = Math.Max(endLine.Value, startLine.GetValueOrDefault(1));
                count = end - (startLine ?? 1) + 1;
            }
            else if (path.Contains("offload_") || path.Contains("tool_outputs"))
            {
                // Reading an offloaded artifact without explicit ranges: default to a bounded
                // window so a whole-file re-read can never re-offload into an infinite loop.
                count = 120;
            }

            // Cap the delivered characters so the result always fits in context and never gets
            // offloaded again (offloaded reads of offload files were looping forever). When the
            // cap is hit, tell the model exactly which range to continue with.
            int charBudget = Math.Max(4000, MaxToolOutputChars - 1500);

            var sb = new StringBuilder();
            int lineIndex = 0;
            int delivered = 0;
            int remaining = count;
            bool capped = false;
            await foreach (var line in File.ReadLinesAsync(path, ct))
            {
                if (lineIndex < start)
                {
                    lineIndex++;
                    continue;
                }
                if (remaining <= 0) break;
                string chunk = line;
                if (sb.Length + chunk.Length + 1 > charBudget)
                {
                    if (sb.Length == 0 && chunk.Length > charBudget)
                    {
                        // Single enormous line: deliver a bounded slice rather than nothing.
                        chunk = chunk.Substring(0, charBudget);
                    }
                    else
                    {
                        capped = true;
                        break;
                    }
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(chunk);
                delivered++;
                remaining--;
                lineIndex++;
            }

            if (delivered == 0)
            {
                return new ToolResult(request.Name, false, "",
                    $"No lines found in the requested range (start_line={(startLine ?? 1)}). The file may be empty or the range starts past its end.");
            }

            if (capped)
            {
                int nextStart = start + delivered + 1;
                sb.Append($"\n\n…[output capped at {charBudget} chars to fit context — {delivered} line(s) delivered (lines {start + 1}-{start + delivered}). Continue reading the next range with start_line={nextStart}, end_line={nextStart + 100}.]");
            }
            else if (!endLine.HasValue && remaining <= 0)
            {
                int nextStart = start + delivered + 1;
                sb.Append($"\n\n…[lines {start + 1}-{start + delivered} delivered; the file continues beyond. Read the next range with start_line={nextStart}, end_line={nextStart + 100}.]");
            }
            return new ToolResult(request.Name, true, sb.ToString(), null);
        }

        var content = await File.ReadAllTextAsync(path, ct);
        return new ToolResult(request.Name, true, content, null);
    }

    private async Task<ToolResult> WriteFileAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        var content = GetStringArg(request.Arguments, "content");
        if (string.IsNullOrEmpty(path)) return InvalidCall(request.Name, "Path is required");
        if (content == null) return InvalidCall(request.Name, "Content is required");
        var commandLike = CommandLikePathResult(request, path);
        if (commandLike != null) return commandLike;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Factual change capture (workbench §7–§8): snapshot the file BEFORE the write, then
        // after, compute a REAL diff and persist it — the Changes tab shows evidence from the
        // filesystem, never model-generated narration. Task-scoped via CurrentTaskId.
        string? beforeContent = null;
        try
        {
            if (File.Exists(path))
            {
                beforeContent = await File.ReadAllTextAsync(path, ct);
            }
        }
        catch { /* unreadable — record as a fresh file */ }

        await File.WriteAllTextAsync(path, content, ct);

        await CaptureFileMutationAsync(request, sessionId, path, beforeContent, content);

        return new ToolResult(request.Name, true, "File written successfully", null);
    }

    /// <summary>
    /// Applies a targeted text replacement inside an existing file (the <c>edit_file</c>
    /// tool). The old_text must appear EXACTLY once — zero or multiple matches fail with
    /// guidance instead of risking a corrupt or ambiguous edit. Every mutation flows through
    /// <see cref="CaptureFileMutationAsync"/>, the same pipeline as write_file, so diffs,
    /// artifacts, and events stay consistent across tools.
    /// </summary>
    private async Task<ToolResult> EditFileAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        var oldText = GetStringArg(request.Arguments, "old_text");
        var newText = GetStringArg(request.Arguments, "new_text");
        if (string.IsNullOrEmpty(path)) return InvalidCall(request.Name, "Path is required");
        if (oldText == null) return InvalidCall(request.Name, "old_text is required");
        if (newText == null) return InvalidCall(request.Name, "new_text is required");
        var commandLike = CommandLikePathResult(request, path);
        if (commandLike != null) return commandLike;
        if (!File.Exists(path)) return new ToolResult(request.Name, false, string.Empty, $"File not found: {path}");

        string beforeContent;
        try
        {
            beforeContent = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, string.Empty, $"Failed to read {path}: {ex.Message}");
        }

        int idx = beforeContent.IndexOf(oldText, StringComparison.Ordinal);
        int replaceLen = oldText.Length;
        bool fuzzyApplied = false;
        if (idx < 0)
        {
            // Exact match failed — try the whitespace/indentation-tolerant fuzzy match before
            // giving up (blueprint TODO 081). A model that read the file and then the file was
            // reformatted (indentation drift, CRLF, trailing whitespace) would otherwise fail a
            // perfectly valid edit. The fuzzy match must still be UNIQUE, and the replacement
            // replaces the exact original span (preserving the file's real whitespace).
            var fuzzy = FindTolerantMatch(beforeContent, oldText, out bool fuzzyAmbiguous);
            if (fuzzy == null)
            {
                return new ToolResult(request.Name, false, string.Empty,
                    fuzzyAmbiguous
                        ? $"old_text appears more than once in {path} (even after normalizing whitespace/indentation). Include more surrounding context so the match is unique, or use write_file to rewrite the whole file."
                        : $"old_text was not found in {path} (whitespace/indentation-insensitive search also found nothing). Use read_file to get the exact current content, or write_file to rewrite the whole file.");
            }
            idx = fuzzy.Value.Start;
            replaceLen = fuzzy.Value.Length;
            fuzzyApplied = true;
        }
        else if (beforeContent.IndexOf(oldText, idx + oldText.Length, StringComparison.Ordinal) >= 0)
        {
            return new ToolResult(request.Name, false, string.Empty,
                $"old_text appears more than once in {path}. Include more surrounding context so the match is unique, or use write_file to rewrite the whole file.");
        }

        string afterContent = beforeContent.Substring(0, idx) + newText + beforeContent.Substring(idx + replaceLen);

        await File.WriteAllTextAsync(path, afterContent, ct);

        await CaptureFileMutationAsync(request, sessionId, path, beforeContent, afterContent);

        return new ToolResult(request.Name, true,
            fuzzyApplied ? "File edited successfully (whitespace/indentation-tolerant match applied)" : "File edited successfully",
            null);
    }

    /// <summary>
    /// Applies a unified diff (diff -u format) to an existing file (the <c>apply_patch</c>
    /// tool, blueprint TODO 082). Parsing and application are pure and whitespace/line-ending
    /// tolerant (see <see cref="Klydis.Core.Workbench.UnifiedDiff"/>); on success the mutation
    /// flows through <see cref="CaptureFileMutationAsync"/>, the same pipeline as write_file /
    /// edit_file, so diffs, artifacts, and events stay consistent.
    /// </summary>
    private async Task<ToolResult> ApplyPatchAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        var patch = GetStringArg(request.Arguments, "patch");
        if (string.IsNullOrEmpty(path)) return InvalidCall(request.Name, "Path is required");
        if (string.IsNullOrEmpty(patch)) return InvalidCall(request.Name, "patch is required");
        var commandLike = CommandLikePathResult(request, path);
        if (commandLike != null) return commandLike;
        if (!File.Exists(path)) return new ToolResult(request.Name, false, string.Empty, $"File not found: {path}");

        string beforeContent;
        try
        {
            beforeContent = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, string.Empty, $"Failed to read {path}: {ex.Message}");
        }

        string? afterContent = Klydis.Core.Workbench.UnifiedDiff.Apply(beforeContent, patch, out string? applyError);
        if (afterContent == null)
        {
            return new ToolResult(request.Name, false, string.Empty, $"Failed to apply patch: {applyError}");
        }
        if (afterContent == beforeContent)
        {
            return new ToolResult(request.Name, false, string.Empty,
                "Patch applied but produced no changes — is the file already patched?");
        }

        await File.WriteAllTextAsync(path, afterContent, ct);
        await CaptureFileMutationAsync(request, sessionId, path, beforeContent, afterContent);
        return new ToolResult(request.Name, true, "Patch applied successfully", null);
    }

    /// <summary>
    /// Whitespace/indentation-tolerant substring match (blueprint TODO 081). Normalizes both the
    /// content and the needle by collapsing every whitespace run to a single space, then searches
    /// for a UNIQUE match; the normalized match is mapped back to original character offsets so
    /// the caller replaces the exact original span (preserving the file's real whitespace).
    /// Returns null when the needle is not found (even fuzzily) or matches more than once
    /// (<paramref name="ambiguous"/> set in the latter case). A needle that is entirely
    /// whitespace is rejected — it carries no edit meaning.
    /// </summary>
    private static (int Start, int Length)? FindTolerantMatch(string content, string needle, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(content)) return null;

        // Normalize the content, tracking each normalized char's original index so the match can
        // be mapped back to exact original offsets. LINE BREAKS are kept distinct from horizontal
        // whitespace: runs of spaces/tabs collapse to a single space and runs of line breaks to a
        // single '\n'. This tolerates indentation / trailing-whitespace / CRLF drift WITHIN a
        // line without letting a match swallow the preceding line break or cross line boundaries.
        var origIndex = new List<int>(content.Length);
        var normChars = new List<char>(content.Length);
        bool lastWasLineBreak = false;
        bool lastWasSpace = false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\n' || c == '\r')
            {
                if (!lastWasLineBreak) { normChars.Add('\n'); origIndex.Add(i); lastWasLineBreak = true; lastWasSpace = false; }
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) { normChars.Add(' '); origIndex.Add(i); lastWasSpace = true; lastWasLineBreak = false; }
            }
            else
            {
                normChars.Add(c); origIndex.Add(i); lastWasSpace = false; lastWasLineBreak = false;
            }
        }
        string normContent = new string(normChars.ToArray());

        var needleNorm = new List<char>(needle.Length);
        bool nLastLineBreak = false;
        bool nLastSpace = false;
        foreach (char c in needle)
        {
            if (c == '\n' || c == '\r')
            {
                if (!nLastLineBreak) { needleNorm.Add('\n'); nLastLineBreak = true; nLastSpace = false; }
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!nLastSpace) { needleNorm.Add(' '); nLastSpace = true; nLastLineBreak = false; }
            }
            else { needleNorm.Add(c); nLastSpace = false; nLastLineBreak = false; }
        }
        string normNeedle = new string(needleNorm.ToArray());
        if (normNeedle.Trim().Length == 0) return null; // all-whitespace needle: no edit meaning

        int first = normContent.IndexOf(normNeedle, StringComparison.Ordinal);
        if (first < 0) return null;
        if (normContent.IndexOf(normNeedle, first + normNeedle.Length, StringComparison.Ordinal) >= 0)
        {
            ambiguous = true;
            return null;
        }

        int origStart = origIndex[first];
        int origEnd = origIndex[first + normNeedle.Length - 1] + 1;
        return (origStart, origEnd - origStart);
    }

    /// <summary>
    /// The ONE mutation pipeline every file-writing tool goes through (write_file, edit_file):
    /// real diff → durable FileChange → artifact registration → execution events. Never
    /// duplicated across tools, so the Changes/Preview/Files panels all derive from the same
    /// factual state. Each step is best-effort — a failed record must never fail the write.
    /// </summary>
    private async Task CaptureFileMutationAsync(ToolCallRequest request, string? sessionId, string path, string? beforeContent, string afterContent)
    {
        bool created = beforeContent == null;

        // 1. Real diff + durable FileChange (the Changes tab).
        try
        {
            var diff = DiffService.Diff(beforeContent, afterContent);
            var change = new FileChange(
                ChangeId: Guid.NewGuid().ToString("N"),
                SessionId: sessionId ?? string.Empty,
                TaskId: CurrentTaskId,
                Path: path,
                Tool: request.Name,
                BeforeHash: HashText(beforeContent),
                AfterHash: HashText(afterContent),
                Diff: diff.Text,
                AddedLines: diff.AddedLines,
                DeletedLines: diff.DeletedLines,
                TimestampUtc: DateTime.UtcNow);
            await messageStore.AddFileChangeAsync(change);
            logger.LogDebug("Captured file change for {Path} (+{Added}/-{Deleted} lines) in session {SessionId}.",
                path, diff.AddedLines, diff.DeletedLines, sessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to capture file change for {Path}.", path);
        }

        // 2. File lifecycle event.
        try
        {
            await EmitExecutionEventAsync(sessionId, created ? "FileCreated" : "FileModified", CurrentTaskId, request.Name, path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit file event for {Path}.", path);
        }

        // 3. Artifact registration (the Preview tab) — only for recognized previewable types.
        try
        {
            await RegisterArtifactAsync(sessionId, path, afterContent, created);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to register artifact for {Path}.", path);
        }
    }

    private static string HashText(string? text)
    {
        if (text == null) return "(new)";
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static readonly HashSet<string> ArtifactExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".md", ".markdown", ".txt", ".json", ".xml", ".log", ".cs", ".ts",
        ".py", ".js", ".css", ".xaml", ".sql", ".ps1", ".bat", ".yml", ".yaml", ".toml",
        ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    /// <summary>
    /// The previewable artifact kind for a path (html/md/text/svg/image), or empty when the
    /// file is not a candidate artifact. Mirrors the Preview panel's renderer selection.
    /// </summary>
    private static string GetArtifactType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "html",
            ".md" or ".markdown" => "md",
            ".svg" => "svg",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "image",
            ".txt" or ".json" or ".xml" or ".log" or ".cs" or ".ts" or ".py" or ".js" or ".css" or ".xaml" or ".sql" or ".ps1" or ".bat" or ".yml" or ".yaml" or ".toml" => "text",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Registers/updates the durable artifact entry for a written file (is_current revision
    /// lifecycle), and emits the artifact lifecycle event. Only recognized previewable types
    /// become artifacts; the FileChange log records every mutation regardless.
    /// </summary>
    private async Task RegisterArtifactAsync(string? sessionId, string path, string content, bool created)
    {
        if (!ArtifactExtensions.Contains(Path.GetExtension(path))) return;

        await messageStore.MarkArtifactsStaleAsync(sessionId ?? string.Empty, path);
        await messageStore.AddArtifactAsync(new ArtifactRow(
            ArtifactId: Guid.NewGuid().ToString("N"),
            SessionId: sessionId ?? string.Empty,
            TaskId: CurrentTaskId,
            Path: path,
            ArtifactType: GetArtifactType(path),
            ContentHash: HashText(content),
            CreatedAtUtc: DateTime.UtcNow,
            UpdatedAtUtc: DateTime.UtcNow,
            Previewable: true,
            IsCurrent: true));

        await EmitExecutionEventAsync(sessionId, created ? "ArtifactCreated" : "ArtifactUpdated", CurrentTaskId, null, path);
    }

    /// <summary>
    /// Appends a durable execution event to the stream (the backbone the workbench projects
    /// from). Best-effort: a failed event record never breaks the operation that produced it.
    /// </summary>
    private async Task EmitExecutionEventAsync(string? sessionId, string eventType, string? taskId, string? toolName, string? path, string? payload = null)
    {
        await messageStore.AddExecutionEventAsync(new ExecutionEventRow(
            EventId: Guid.NewGuid().ToString("N"),
            SessionId: sessionId ?? string.Empty,
            TaskId: taskId,
            RunId: CurrentRunId,
            EventType: eventType,
            TimestampUtc: DateTime.UtcNow,
            ToolName: toolName,
            Path: path,
            PayloadJson: payload));
    }

    private Task<ToolResult> ListDirectoryAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        if (string.IsNullOrEmpty(path)) return Task.FromResult(InvalidCall(request.Name, "Path is required"));
        var commandLike = CommandLikePathResult(request, path);
        if (commandLike != null) return Task.FromResult(commandLike);
        if (!Directory.Exists(path)) return Task.FromResult(new ToolResult(request.Name, false, "", "Directory not found"));

        var info = new DirectoryInfo(path);
        var entries = new List<string>();
        foreach (var dir in info.GetDirectories())
            entries.Add($"[DIR] {dir.Name}");
        foreach (var file in info.GetFiles())
            entries.Add($"[FILE] {file.Name} ({file.Length} bytes)");

        return Task.FromResult(new ToolResult(request.Name, true, string.Join("\n", entries), null));
    }

    private async Task<ToolResult> RunCommandAsync(ToolCallRequest request, CancellationToken ct)
    {
        var command = GetStringArg(request.Arguments, "command");
        if (string.IsNullOrEmpty(command)) return InvalidCall(request.Name, "Command is required");

        var workingDir = GetStringArg(request.Arguments, "working_directory");
        if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
        {
            // P0: default to the task workspace root when established — not the process cwd,
            // which may be anywhere (and may not even exist as a project boundary).
            workingDir = WorkspaceRoot ?? Directory.GetCurrentDirectory();
        }

        int timeoutMs = 60000;
        if (request.Arguments != null && request.Arguments.TryGetValue("timeout_seconds", out var timeoutObj))
        {
            var unwrapped = UnwrapJsonElement(timeoutObj);
            if (unwrapped != null && int.TryParse(unwrapped.ToString(), out int sec) && sec > 0)
            {
                timeoutMs = Math.Clamp(sec, 5, 300) * 1000;
            }
        }

        // Auto-normalize common PowerShell parameter binding mistakes (e.g. `start chrome --new-window http...` -> `Start-Process chrome -ArgumentList '--new-window', 'http...'`)
        var sanitizedCmd = Regex.Replace(command, @"(?<=\s|^)&&(?=\s|$)", ";");
        var matchStartFlags = Regex.Match(sanitizedCmd, @"^(?:start|Start-Process)\s+([a-zA-Z0-9_\-\.\:\\]+)\s+(--?[a-zA-Z0-9_\-\.]+.*)$", RegexOptions.IgnoreCase);
        if (matchStartFlags.Success)
        {
            string appName = matchStartFlags.Groups[1].Value;
            string rawArgs = matchStartFlags.Groups[2].Value;
            sanitizedCmd = $"Start-Process -FilePath \"{appName}\" -ArgumentList {rawArgs}";
        }

        sanitizedCmd = NormalizePowershellWrapper(sanitizedCmd);

        var encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(sanitizedCmd));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedCmd}",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return new ToolResult(request.Name, false, "", "Failed to start process");

            // Read stdout and stderr asynchronously concurrently with process execution to avoid pipe deadlocks
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (timeoutCts.IsCancellationRequested)
                {
                    return new ToolResult(request.Name, false, "", $"Command timed out after {timeoutMs / 1000} seconds.");
                }
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrEmpty(stderr) && stderr.Contains("CLIXML", StringComparison.OrdinalIgnoreCase))
            {
                stderr = System.Text.RegularExpressions.Regex.Replace(stderr, @"#<\s*CLIXML[\s\S]*?</Objs>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            }

            var output = stdout;
            if (!string.IsNullOrEmpty(stderr))
            {
                if (string.IsNullOrEmpty(output)) output = stderr;
                else output += $"\nSTDERR:\n{stderr}";
            }

            // Unknown-cmdlet feedback: PowerShell's "The term 'X' is not recognized as the name of
            // a cmdlet..." is the ground-truth signal for a hallucinated command token (the KMS
            // chats invented Get-RandomProperty / Write-OutnFile / slpapi.exe and got zero useful
            // feedback). Extract the first unknown token and say exactly what was wrong — the
            // model can then replace that ONE token instead of guessing.
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                var unknown = Regex.Match(stderr, @"The term\s+['""]([^'""]+)['""]\s+is not recognized", RegexOptions.IgnoreCase);
                if (unknown.Success && !string.IsNullOrWhiteSpace(unknown.Groups[1].Value))
                {
                    output = $"[Unknown command: '{unknown.Groups[1].Value}' — that is not a real cmdlet/command. Replace it with a valid PowerShell cmdlet or the correct tool.]\n\n{output}";
                }
            }

            // GUI-dialog detection: a process that exits 0 with EMPTY console output may have
            // opened a GUI window instead (slmgr /dli, slmgr /ckmsctl, ...). Treating that as
            // "Command executed successfully with no output." lets the model mistake a dialog
            // for evidence — the observed "we received visual outputs" dead-end. Known
            // dialog-launcher verbs get a distinct signal naming the console variant.
            if (string.IsNullOrWhiteSpace(output) && process.ExitCode == 0)
            {
                var firstToken = Regex.Match(sanitizedCmd, @"^\s*([a-zA-Z0-9_\-\.]+)").Groups[1].Value;
                // slmgr invoked directly opens a GUI dialog (the console variant is
                // cscript //nologo slmgr.vbs); wscript is the GUI twin of cscript; slui is the
                // activation UI; control opens the Control Panel. cscript itself is the CONSOLE
                // host and must NOT be flagged.
                bool guiLauncher = firstToken.Equals("slmgr", StringComparison.OrdinalIgnoreCase) ||
                                   firstToken.Equals("slui", StringComparison.OrdinalIgnoreCase) ||
                                   firstToken.Equals("wscript", StringComparison.OrdinalIgnoreCase) ||
                                   firstToken.Equals("control", StringComparison.OrdinalIgnoreCase);
                output = guiLauncher
                    ? $"[This command opened a GUI dialog; its console output is unavailable. Use the console variant to get capturable output: cscript //nologo %windir%\\system32\\slmgr.vbs /dli]"
                    : "Command executed successfully with no output.";
            }

            return new ToolResult(request.Name, process.ExitCode == 0, output, process.ExitCode != 0 ? $"Command exited with code {process.ExitCode}" : null,
                ExitCode: process.ExitCode);
        }
        catch (Exception ex)
        {
            // Cancellation must PROPAGATE to the dispatch-level per-tool-call timeout handler
            // (which produces the actionable "exceeded the per-call timeout" message). The
            // old swallow-and-convert leaked a bare "The operation was canceled." to the
            // model, and it made the ToolExecutor.ToolCallTimeout backstop dead for run_command.
            if (ex is OperationCanceledException) throw;
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

#pragma warning disable CA1416 // Validate platform compatibility
    private Task<ToolResult> GetSystemInfoAsync(CancellationToken ct)
    {
        var infoList = new List<string>
        {
            $"OS: {Environment.OSVersion}"
        };

        try
        {
            // CPU name and core details
            string cpuName = "Unknown CPU";
            int coreCount = 0;
            int logicalProcessors = 0;
            using (var processorSearcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                foreach (var obj in processorSearcher.Get())
                {
                    cpuName = obj["Name"]?.ToString()?.Trim() ?? cpuName;
                    coreCount = Convert.ToInt32(obj["NumberOfCores"] ?? 0);
                    logicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);
                    break;
                }
            }
            infoList.Add($"CPU: {cpuName} ({coreCount} Cores, {logicalProcessors} Logical Processors)");

            // RAM details
            double totalRamGb = 0;
            double availableRamGb = 0;
            using (var computerSystemSearcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                foreach (var obj in computerSystemSearcher.Get())
                {
                    ulong totalRamBytes = Convert.ToUInt64(obj["TotalPhysicalMemory"] ?? 0);
                    totalRamGb = totalRamBytes / (1024.0 * 1024.0 * 1024.0);
                    break;
                }
            }
            using (var osSearcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem"))
            {
                foreach (var obj in osSearcher.Get())
                {
                    ulong freeRamKb = Convert.ToUInt64(obj["FreePhysicalMemory"] ?? 0);
                    availableRamGb = freeRamKb / (1024.0 * 1024.0);
                    break;
                }
            }
            infoList.Add($"RAM: {Math.Round(totalRamGb, 2)} GB total ({Math.Round(availableRamGb, 2)} GB available)");
            infoList.Add($"Process Working Set: {Environment.WorkingSet / (1024 * 1024)} MB");

            // Disk details
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
            var diskInfo = string.Join(", ", drives.Select(d => $"{d.Name} ({d.TotalFreeSpace / (1024 * 1024 * 1024)}GB free of {d.TotalSize / (1024 * 1024 * 1024)}GB)"));
            infoList.Add($"Disks: {diskInfo}");

            // GPU details
            var gpus = new List<string>();
            using (var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController"))
            {
                foreach (var obj in searcher.Get())
                {
                    gpus.Add(obj["Name"]?.ToString() ?? "Unknown GPU");
                }
            }
            if (gpus.Any())
            {
                infoList.Add($"GPU(s): {string.Join(", ", gpus)}");
            }
        }
        catch (Exception ex)
        {
            infoList.Add($"[Hardware query failed: {ex.Message}]");
        }

        var info = string.Join("\n", infoList);
        return Task.FromResult(new ToolResult("get_system_info", true, info, null));
    }
#pragma warning restore CA1416

    private static string UnwrapBingUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.Contains("bing.com/ck/a") && url.Contains("u=a1"))
        {
            var match = Regex.Match(url, @"u=a1([a-zA-Z0-9_\-]+)");
            if (match.Success)
            {
                try
                {
                    string b64 = match.Groups[1].Value.Replace('-', '+').Replace('_', '/');
                    switch (b64.Length % 4)
                    {
                        case 2: b64 += "=="; break;
                        case 3: b64 += "="; break;
                    }
                    var bytes = Convert.FromBase64String(b64);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch { }
            }
        }
        return url;
    }

    private async Task<ToolResult> SearchWebAsync(ToolCallRequest request, CancellationToken ct)
    {
        var query = GetStringArg(request.Arguments, "query");
        if (string.IsNullOrEmpty(query)) return InvalidCall(request.Name, "Query is required");

        int maxResults = 5;
        if (request.Arguments != null && request.Arguments.TryGetValue("max_results", out var mrObj))
        {
            var unwrapped = UnwrapJsonElement(mrObj);
            if (unwrapped != null && int.TryParse(unwrapped.ToString(), out int r) && r > 0)
            {
                maxResults = Math.Clamp(r, 1, 10);
            }
        }

        try
        {
            var results = new List<string>();

            // Tier 1: Stealth Browser Bing Search (or HttpClient Bing Search if stealth service unavailable)
            try
            {
                var bingEndpoint = Environment.GetEnvironmentVariable("BING_SEARCH_ENDPOINT") ?? "https://www.bing.com/search";
                var searchUrl = $"{bingEndpoint}?q={Uri.EscapeDataString(query)}";
                string? html = null;

                if (_stealthBrowserService != null)
                {
                    html = await _stealthBrowserService.RenderPageHtmlAsync(searchUrl, ct);
                }

                if (string.IsNullOrEmpty(html))
                {
                    var requestMsg = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                    requestMsg.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                    var response = await _httpClient.SendAsync(requestMsg, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        html = await response.Content.ReadAsStringAsync(ct);
                    }
                }

                if (!string.IsNullOrEmpty(html))
                {
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    
                    var algoNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'b_algo')]");
                    if (algoNodes != null)
                    {
                        foreach (var node in algoNodes.Take(maxResults))
                        {
                            var titleNode = node.SelectSingleNode(".//h2/a") ?? node.SelectSingleNode(".//a");
                            var title = titleNode != null ? HtmlEntity.DeEntitize(titleNode.InnerText).Trim() : "No Title";
                            var rawLink = titleNode != null ? titleNode.GetAttributeValue("href", "") : "";
                            var cleanLink = UnwrapBingUrl(rawLink);
                            
                            var snippetNode = node.SelectSingleNode(".//p") ?? node.SelectSingleNode(".//div[contains(@class, 'b_caption')]/p") ?? node.SelectSingleNode(".//span[contains(@class, 'b_snippet')]") ?? node.SelectSingleNode(".//span");
                            var snippet = snippetNode != null ? HtmlEntity.DeEntitize(snippetNode.InnerText).Trim() : "No Snippet";
                            
                            snippet = Regex.Replace(snippet, @"\s+", " ");
                            results.Add($"Title: {title}\nLink: {cleanLink}\nSnippet: {snippet}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bing search failed; trying DuckDuckGo Lite fallback.");
            }

            // Tier 2: DuckDuckGo Lite Search
            if (results.Count == 0)
            {
                try
                {
                    var ddgUrl = Environment.GetEnvironmentVariable("DUCKDUCKGO_SEARCH_ENDPOINT") ?? "https://lite.duckduckgo.com/lite/";
                    var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", query) });
                    var requestMsg = new HttpRequestMessage(HttpMethod.Post, ddgUrl) { Content = content };
                    requestMsg.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                    var response = await _httpClient.SendAsync(requestMsg, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var html = await response.Content.ReadAsStringAsync(ct);
                        var doc = new HtmlDocument();
                        doc.LoadHtml(html);

                        var resultNodes = doc.DocumentNode.SelectNodes("//td[contains(@class, 'result-snippet')]");
                        var linkNodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'result-link')]");

                        if (linkNodes != null)
                        {
                            int count = Math.Min(linkNodes.Count, maxResults);
                            for (int i = 0; i < count; i++)
                            {
                                var title = HtmlEntity.DeEntitize(linkNodes[i].InnerText).Trim();
                                var link = linkNodes[i].GetAttributeValue("href", "");
                                var snippet = (resultNodes != null && i < resultNodes.Count) ? HtmlEntity.DeEntitize(resultNodes[i].InnerText).Trim() : "No Snippet";
                                results.Add($"Title: {title}\nLink: {link}\nSnippet: {snippet}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "DuckDuckGo Lite fallback failed; trying Wikipedia search.");
                }
            }

            // Tier 3: Wikipedia OpenSearch
            if (results.Count == 0)
            {
                try
                {
                    var wikiEndpoint = Environment.GetEnvironmentVariable("WIKIPEDIA_API_ENDPOINT") ?? "https://en.wikipedia.org/w/api.php";
                    var wikiUrl = $"{wikiEndpoint}?action=opensearch&search={Uri.EscapeDataString(query)}&limit={maxResults}&namespace=0&format=json";
                    var requestMsg = new HttpRequestMessage(HttpMethod.Get, wikiUrl);
                    requestMsg.Headers.Add("User-Agent", "KlydisAssistant/1.0 (contact: info@klydis.local)");
                    
                    var response = await _httpClient.SendAsync(requestMsg, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var wikiResponse = await response.Content.ReadAsStringAsync(ct);
                        using var docJson = JsonDocument.Parse(wikiResponse);
                        var root = docJson.RootElement;
                        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 4)
                        {
                            var titles = root[1];
                            var descriptions = root[2];
                            var urls = root[3];
                            int count = Math.Min(titles.GetArrayLength(), maxResults);
                            for (int i = 0; i < count; i++)
                            {
                                results.Add($"Title: {titles[i].GetString()}\nLink: {urls[i].GetString()}\nSnippet: {descriptions[i].GetString()}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Wikipedia search fallback failed.");
                }
            }

            if (results.Count == 0)
            {
                return new ToolResult(request.Name, true, "No results found.", null);
            }
            return new ToolResult(request.Name, true, string.Join("\n\n---\n\n", results), null);
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

    private async Task<ToolResult> CrawlUrlAsync(ToolCallRequest request, CancellationToken ct)
    {
        var url = GetStringArg(request.Arguments, "url");
        if (string.IsNullOrEmpty(url)) return InvalidCall(request.Name, "URL is required");

        // Tier 1: fast plain-HTTP fetch. Works without any browser binaries and is the most
        // reliable path for static, server-rendered pages (weather, news, documentation).
        try
        {
            var httpMarkdown = await FetchPageViaHttpAsync(url, ct);
            if (!string.IsNullOrWhiteSpace(httpMarkdown) && httpMarkdown.Length > 200)
            {
                logger.LogInformation("CrawlUrlAsync served via plain HTTP fetch for URL: {Url}", url);
                return new ToolResult(request.Name, true, httpMarkdown, null);
            }

            logger.LogInformation("HTTP fetch returned insufficient content for URL: {Url}; falling back to browser rendering.", url);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HTTP fetch failed for URL: {Url}; falling back to browser rendering.", url);
        }

        string? lastError = null;

        // SSRF + scheme guard for the browser tiers: FetchPageViaHttpAsync validates http/https
        // and rejects private hosts, but the browser paths below accept ANY URL — a prompt-
        // injected model could otherwise bypass the guard entirely by falling through to a
        // browser fetch of file:// URLs or internal/cloud-metadata hosts.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var crawlUri) ||
            (crawlUri.Scheme != Uri.UriSchemeHttp && crawlUri.Scheme != Uri.UriSchemeHttps))
        {
            return new ToolResult(request.Name, false, "", "Only http/https URLs are supported.");
        }
        if (await IsPrivateHostAsync(crawlUri.Host))
        {
            return new ToolResult(request.Name, false, "",
                $"Host '{crawlUri.Host}' resolves to a private or internal address, which cannot be crawled.");
        }

        // Tier 2: stealth browser (renders JavaScript, bypasses anti-bot checks). Auto-installs
        // Playwright Chromium on first use when the binaries are missing.
        if (_stealthBrowserService != null)
        {
            try
            {
                logger.LogInformation("CrawlUrlAsync using StealthBrowserService for URL: {Url}", url);
                var stealthResult = await _stealthBrowserService.CrawlUrlAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(stealthResult))
                {
                    return new ToolResult(request.Name, true, stealthResult, null);
                }
                lastError = "Browser crawl returned empty content.";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                logger.LogWarning(ex, "Stealth browser crawl failed for URL: {Url}", url);
            }
        }

        // Tier 3: basic Playwright if stealth service is unavailable
        try
        {
            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();

            await page.GotoAsync(url, new Microsoft.Playwright.PageGotoOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle, Timeout = 20000 });

            string title = await page.TitleAsync();

            await page.EvaluateAsync(@"() => {
                const noisySelectors = ['nav', 'footer', 'header', 'aside', '[role=""navigation""]', '[role=""banner""]', '.cookie-banner', '.ad-container', 'iframe'];
                noisySelectors.forEach(s => document.querySelectorAll(s).forEach(el => el.remove()));
            }");

            string html = "";
            var mainHandle = await page.QuerySelectorAsync("main, article, [role=\"main\"]");
            if (mainHandle != null)
            {
                html = await mainHandle.InnerHTMLAsync();
            }
            else
            {
                html = await page.InnerHTMLAsync("body");
            }

            var markdown = HtmlToMarkdown(html);

            var header = $"# Page Title: {title}\nSource URL: {url}\n\n---\n\n";
            var fullOutput = header + markdown;

            if (fullOutput.Length > 20000) fullOutput = fullOutput[..20000] + "\n\n... [TRUNCATED]";

            return new ToolResult(request.Name, true, fullOutput, null);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            logger.LogWarning(ex, "Raw Playwright crawl failed for URL: {Url}", url);
        }

        var finalMessage = string.IsNullOrWhiteSpace(lastError)
            ? "Failed to crawl URL."
            : $"Failed to crawl URL. {lastError}";
        if (IsBrowserMissingError(lastError))
        {
            // Do not surface raw Playwright installer instructions to the model — tell it the
            // browser is being installed on first use and to retry shortly.
            finalMessage = "Browser binaries are missing and the automatic one-time installation (a ~160 MB download) has not completed, so the page could not be fetched via a browser. Please call crawl_url again in a minute; meanwhile the direct HTTP fetch path continues to work for static pages.";
        }

        return new ToolResult(request.Name, false, "", finalMessage);
    }

    /// <summary>
    /// True when a browser exception means Playwright's binaries are not installed yet
    /// (as opposed to a page-level navigation/network failure).
    /// </summary>
    private static bool IsBrowserMissingError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("browser binaries not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("executable path doesn't exist", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("playwright.ps1", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("ms-playwright", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("host executable doesn't exist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fetches a page over plain HTTP and converts its main content to Markdown.
    /// Returns null when the page has no meaningful content (e.g. a JavaScript-only shell).
    /// </summary>
    private static async Task<string?> FetchPageViaHttpAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only http/https URLs are supported.");
        }

        // SSRF guard: never fetch private/internal hosts over the direct HTTP path.
        if (await IsPrivateHostAsync(uri.Host))
        {
            throw new ArgumentException($"Host '{uri.Host}' resolves to a private or internal address, which cannot be crawled.");
        }

        using var requestMsg = new HttpRequestMessage(HttpMethod.Get, uri);
        requestMsg.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        requestMsg.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        requestMsg.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await _httpClient.SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} while fetching the page.");
        }

        var html = await response.Content.ReadAsStringAsync(cts.Token);
        if (string.IsNullOrWhiteSpace(html)) return null;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var node in doc.DocumentNode.Descendants()
                     .Where(n => n.Name is "script" or "style" or "noscript" or "nav" or "footer" or "header" or "aside" or "iframe" or "form" or "svg" or "canvas")
                     .ToList())
        {
            node.Remove();
        }

        var mainNode = doc.DocumentNode.SelectSingleNode("//main | //article | //*[@role='main']");
        var contentNode = mainNode ?? doc.DocumentNode;

        var markdown = HtmlToMarkdown(contentNode.InnerHtml);
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? uri.Host;
        var header = $"# Page Title: {title}\nSource URL: {url}\n\n---\n\n";
        var fullOutput = header + markdown;

        if (fullOutput.Length > 20000) fullOutput = fullOutput[..20000] + "\n\n... [TRUNCATED]";

        return fullOutput;
    }

    private static string HtmlToMarkdown(string html)
    {
#pragma warning disable CS0618
        var config = new ReverseMarkdown.Config
        {
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true
        };
#pragma warning restore CS0618
        var converter = new ReverseMarkdown.Converter(config);
        var markdown = converter.Convert(html);

        markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n").Trim();
        return markdown;
    }

    /// <summary>
    /// Returns true when the host resolves to a loopback, private, link-local, or otherwise
    /// internal address. Prevents the crawl tool from being used as an SSRF vector.
    /// </summary>
    private static async Task<bool> IsPrivateHostAsync(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            return IsPrivateAddress(parsed);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.Any(IsPrivateAddress);
        }
        catch
        {
            // If DNS resolution fails, let the actual request surface the error.
            return false;
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var ip = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

            if ((ip & 0xFF000000) == 0x0A000000) return true;  // 10.0.0.0/8
            if ((ip & 0xFFF00000) == 0xAC100000) return true;  // 172.16.0.0/12
            if ((ip & 0xFFFF0000) == 0xC0A80000) return true;  // 192.168.0.0/16
            if ((ip & 0xFFFF0000) == 0xA9FE0000) return true;  // 169.254.0.0/16 link-local
            if ((ip & 0xFFC00000) == 0x64400000) return true;  // 100.64.0.0/10 CGNAT
            return ip == 0;                                    // 0.0.0.0/8
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;                      // fc00::/7 unique local
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;  // fe80::/10 link-local
            if (bytes[0] == 0xFF) return true;                               // multicast
            return bytes.All(b => b == 0);                                   // ::
        }

        return false;
    }

    private async Task<ToolResult> SearchFilesAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        var pattern = GetStringArg(request.Arguments, "pattern") ?? "*.*";
        var contains = GetStringArg(request.Arguments, "contains");

        if (string.IsNullOrEmpty(path)) return InvalidCall(request.Name, "Valid path is required");
        var commandLike = CommandLikePathResult(request, path);
        if (commandLike != null) return commandLike;
        if (!Directory.Exists(path)) return new ToolResult(request.Name, false, "", "Valid path is required");

        try
        {
            var results = new List<string>();
            const int maxResults = 20;

            foreach (var file in EnumerateFilesResilient(path, pattern))
            {
                if (results.Count >= maxResults)
                {
                    results.Add("... [TRUNCATED 20+ RESULTS]");
                    break;
                }

                if (!string.IsNullOrEmpty(contains))
                {
                    try
                    {
                        // Skip binary files and cap per-file reads so a binary blob or an
                        // enormous log cannot stall the search or blow up memory.
                        if (!await FileContainsTextAsync(file, contains, ct))
                            continue;
                    }
                    catch
                    {
                        continue; // unreadable/binary file: skip, don't kill the whole search
                    }
                }

                results.Add(file);
            }

            return new ToolResult(request.Name, true, results.Count > 0 ? string.Join("\n", results) : "No files matched.", null);
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

    /// <summary>
    /// Recursively enumerates files without letting one unreadable subdirectory abort the whole
    /// search (EnumerateFiles with AllDirectories throws mid-iteration on the first denied dir).
    /// </summary>
    private static IEnumerable<string> EnumerateFilesResilient(string root, string pattern)
    {
        IEnumerable<string> files;
        IEnumerable<string> dirs;
        try
        {
            files = Directory.EnumerateFiles(root, pattern);
        }
        catch
        {
            files = Array.Empty<string>();
        }
        try
        {
            dirs = Directory.EnumerateDirectories(root);
        }
        catch
        {
            dirs = Array.Empty<string>();
        }

        foreach (var file in files)
        {
            yield return file;
        }
        foreach (var dir in dirs)
        {
            foreach (var file in EnumerateFilesResilient(dir, pattern))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="file"/> is a text file containing <paramref name="needle"/>
    /// (case-insensitive). Reads at most the first 5 MB and rejects NUL-heavy binary content.
    /// </summary>
    private static async Task<bool> FileContainsTextAsync(string file, string needle, CancellationToken ct)
    {
        const int maxReadBytes = 5 * 1024 * 1024;
        var fileInfo = new FileInfo(file);
        if (fileInfo.Length > maxReadBytes * 4)
        {
            // Very large file: still searchable, but only inspect the head to keep the search bounded.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[maxReadBytes];
            int read = await stream.ReadAsync(buffer.AsMemory(0, maxReadBytes), ct);
            if (ContainsNulByte(buffer.AsSpan(0, read))) return false;
            return Encoding.UTF8.GetString(buffer, 0, read).Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        var content = await File.ReadAllTextAsync(file, ct);
        if (content.Length > 0 && content.IndexOf('\0') >= 0) return false;
        return content.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsNulByte(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
        {
            if (b == 0) return true;
        }
        return false;
    }

    private async Task<ToolResult> StoreMemoryAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        var fact = GetStringArg(request.Arguments, "fact");
        if (string.IsNullOrEmpty(fact)) return InvalidCall(request.Name, "Fact is required");

        var session = await messageStore.GetSessionAsync(sessionId);
        if (session == null) return new ToolResult(request.Name, false, "", "Session not found");

        // H3: Deduplicate — skip if a normalized version of this fact already exists
        var normalizedFact = fact.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(session.WorldState))
        {
            var existingLines = session.WorldState.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool alreadyStored = existingLines.Any(l => l.TrimStart('-', ' ').Trim().ToLowerInvariant() == normalizedFact);
            if (alreadyStored)
                return new ToolResult(request.Name, true, "Fact already exists in session World State (skipped duplicate).", null);
        }

        var newWorldState = string.IsNullOrEmpty(session.WorldState) ? fact : $"{session.WorldState}\n- {fact}";

        // H3: Cap WorldState to prevent unbounded token growth (~2,000 tokens at 4 chars/token)
        const int MaxWorldStateChars = 8000;
        if (newWorldState.Length > MaxWorldStateChars)
        {
            // Trim oldest lines from the front, preserving the most recent facts
            int excess = newWorldState.Length - MaxWorldStateChars;
            int firstNewline = newWorldState.IndexOf('\n', excess);
            newWorldState = firstNewline >= 0 
                ? "[...older facts trimmed...]\n" + newWorldState[(firstNewline + 1)..]  
                : newWorldState[^MaxWorldStateChars..];
        }

        await messageStore.UpdateSessionAsync(sessionId, null, newWorldState, null);
        return new ToolResult(request.Name, true, "Fact stored successfully in session World State.", null);
    }

    private async Task<ToolResult> RetrieveMemoryAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        var query = GetStringArg(request.Arguments, "query");
        if (string.IsNullOrEmpty(query)) return InvalidCall(request.Name, "Query is required");

        // C1/H4/H8: Fetch more candidates so filtering doesn't leave us empty, then filter to
        // User/Assistant roles only. Exclude injected system/tool messages to prevent the tool
        // from returning its own invocation or raw tool JSON as a memory result.
        var allResults = await messageStore.SearchMessagesAsync(sessionId, query, 15);
        var results = allResults
            .Where(r => r.Message.Role == ChatRole.User || r.Message.Role == ChatRole.Assistant)
            .Where(r => !r.Message.Content.StartsWith("[Tool ", StringComparison.OrdinalIgnoreCase))
            .Where(r => !r.Message.Content.StartsWith("[System ", StringComparison.OrdinalIgnoreCase))
            .Where(r => !r.Message.Content.StartsWith("[SYSTEM", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        if (results.Count == 0) return new ToolResult(request.Name, true, "No relevant past messages found.", null);

        // Truncate individual messages to keep memory context compact (max 500 chars each)
        var output = string.Join("\n\n", results.Select(r =>
        {
            var content = r.Message.Content.Length > 500 
                ? r.Message.Content[..500] + "...[truncated]" 
                : r.Message.Content;
            return $"[{r.Message.Timestamp:HH:mm}] {r.Message.Role}: {content}";
        }));
        return new ToolResult(request.Name, true, output, null);
    }

    private async Task<ToolResult> SummarizeContextAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        try
        {
            await contextOrchestrator.ConsolidateWorldStateAsync(sessionId);
            return new ToolResult(request.Name, true, "Context summarized and world state updated.", null);
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

    private Task<ToolResult> CheckMessageQueueAsync(string sessionId)
    {
        if (MessageQueue == null)
        {
            return Task.FromResult(new ToolResult("check_message_queue", true, "Message queue service is not available.", null));
        }

        // Task-scoped (P0.7): the queue diagnostic reports only the current task's pending
        // messages — other-task items are invisible, consistent with incorporate_queued_message.
        var pending = MessageQueue.GetPending(sessionId, CurrentTaskId);
        if (pending.Count == 0)
        {
            return Task.FromResult(new ToolResult("check_message_queue", true, "No pending messages in the queue.", null));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Pending Queued Messages ({pending.Count}):");
        foreach (var msg in pending)
        {
            sb.AppendLine($"- Queue ID: {msg.Id} | Mode: {msg.Mode} | Status: {msg.Status} | Created: {msg.CreatedAt:HH:mm:ss}");
            sb.AppendLine($"  Content: \"{msg.Content}\"");
        }
        sb.AppendLine("\nTo incorporate any of these messages into your active execution context, call tool 'incorporate_queued_message' with argument {\"queue_id\": \"<ID>\"}.");

        return Task.FromResult(new ToolResult("check_message_queue", true, sb.ToString().TrimEnd(), null));
    }

    private Task<ToolResult> IncorporateQueuedMessageAsync(ToolCallRequest request, string sessionId)
    {
        if (MessageQueue == null)
        {
            return Task.FromResult(new ToolResult("incorporate_queued_message", false, "", "Message queue service is not available."));
        }

        var queueIdStr = GetStringArg(request.Arguments, "queue_id");
        QueuedMessage? msg = null;

        if (!string.IsNullOrEmpty(queueIdStr) && Guid.TryParse(queueIdStr, out var queueId))
        {
            msg = MessageQueue.GetById(queueId, sessionId);
        }

        if (msg == null)
        {
            // Task-scoped reads: the model may only incorporate queued messages that belong
            // to the CURRENT task. Old-task or untagged legacy items are invisible to it —
            // isolation is enforced by the runtime, not by prompting.
            var pendingSteer = MessageQueue.GetPendingSteer(sessionId, CurrentTaskId);
            msg = pendingSteer.FirstOrDefault() ?? MessageQueue.GetPending(sessionId, CurrentTaskId).FirstOrDefault();
        }

        if (msg == null)
        {
            return Task.FromResult(new ToolResult("incorporate_queued_message", false, "", "No pending queued message found to incorporate."));
        }

        if (msg.Status != QueuedMessageStatus.Queued)
        {
            return Task.FromResult(new ToolResult("incorporate_queued_message", false, "", $"Queued message '{msg.Id}' cannot be incorporated because its status is {msg.Status}."));
        }

        MessageQueue.MarkStatus(msg.Id, QueuedMessageStatus.Incorporated);
        string resultText = $"Successfully incorporated queued steering message [ID: {msg.Id} | Mode: {msg.Mode}]: \"{msg.Content}\"";
        return Task.FromResult(new ToolResult("incorporate_queued_message", true, resultText, null));
    }

    private async Task<ToolResult> CreateCustomToolAsync(ToolCallRequest request, CancellationToken ct)
    {
        var name = GetStringArg(request.Arguments, "name");
        var desc = GetStringArg(request.Arguments, "description");
        var lang = GetStringArg(request.Arguments, "language")?.ToLowerInvariant() ?? "powershell";
        var schema = GetStringArg(request.Arguments, "parameters_schema");
        var script = GetStringArg(request.Arguments, "script_content");

        if (string.IsNullOrEmpty(name)) return InvalidCall(request.Name, "Name is required");
        if (string.IsNullOrEmpty(desc)) return InvalidCall(request.Name, "Description is required");
        if (string.IsNullOrEmpty(lang)) return InvalidCall(request.Name, "Language is required");
        if (string.IsNullOrEmpty(schema)) return InvalidCall(request.Name, "Parameters schema is required");
        if (string.IsNullOrEmpty(script)) return InvalidCall(request.Name, "Script content is required");

        if (lang != "powershell" && lang != "python" && lang != "csharp")
        {
            return new ToolResult(request.Name, false, "", "Language must be 'powershell', 'python', or 'csharp'.");
        }

        var nameError = ValidateCustomToolName(name, _tools.Select(t => t.Name));
        if (nameError != null)
        {
            return InvalidCall(request.Name, nameError);
        }

        // Validate schema is parseable
        try { JsonSerializer.Deserialize<List<ToolParameter>>(schema, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) { return new ToolResult(request.Name, false, "", $"Invalid parameters_schema JSON: {ex.Message}"); }

        // H5: Validate PowerShell script syntax before persisting to prevent silent runtime failures
        if (lang == "powershell")
        {
            try
            {
                var escapedScript = script.Replace("'", "''");
                var validateCmd = $"$null = [System.Management.Automation.ScriptBlock]::Create('{escapedScript}')";
                var encodedValidate = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(validateCmd));
                var validatePsi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedValidate}",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var validateProc = Process.Start(validatePsi);
                if (validateProc != null)
                {
                    // Read both streams concurrently (a long parse-error list can exceed the
                    // stderr pipe buffer and deadlock a sequential read) and bound the wait.
                    var validateOutTask = validateProc.StandardOutput.ReadToEndAsync(ct);
                    var validateErrTask = validateProc.StandardError.ReadToEndAsync(ct);

                    using var validateTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var validateLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, validateTimeoutCts.Token);
                    try
                    {
                        await validateProc.WaitForExitAsync(validateLinked.Token);
                    }
                    catch (OperationCanceledException) when (validateTimeoutCts.IsCancellationRequested)
                    {
                        try { validateProc.Kill(); } catch { /* ignore */ }
                        logger.LogWarning("PowerShell syntax validation timed out for custom tool '{ToolName}'", name);
                    }

                    // Drain both streams (they hit EOF once the process exits).
                    await validateOutTask;
                    var syntaxErr = await validateErrTask;
                    if (validateProc.ExitCode != 0)
                    {
                        return new ToolResult(request.Name, false, "", 
                            $"PowerShell syntax validation failed — tool NOT created.\nError: {syntaxErr.Trim()}\nFix the script and try again.");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PowerShell syntax validation step failed for custom tool '{ToolName}'", name);
                // Non-fatal: if validation process itself fails, proceed with a warning
            }
        }

        var record = new Klydis.Core.Memory.CustomToolRecord(name, desc, schema, script, lang, DateTime.UtcNow);
        await messageStore.CreateCustomToolAsync(record);
        InvalidateCustomToolsCache();

        return new ToolResult(request.Name, true, $"Custom tool '{name}' created successfully. It is now available for use.", null);
    }

    /// <summary>
    /// Validates a custom tool name. Names must be callable by the model (the qwen-native parser
    /// only accepts [a-zA-Z0-9_.-]) and must not collide with a built-in tool — a shadowed
    /// custom tool would never execute because dispatch always routes to the built-in.
    /// Returns an error message, or null when the name is valid.
    /// </summary>
    internal static string? ValidateCustomToolName(string? name, IEnumerable<string> builtInNames)
    {
        if (string.IsNullOrEmpty(name)) return "Name is required";
        if (!Regex.IsMatch(name, @"^[a-zA-Z0-9_.\-]+$"))
        {
            return "Invalid tool name. Use only letters, digits, underscores, dots, and dashes (no spaces).";
        }
        if (builtInNames.Any(b => b.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Tool name '{name}' conflicts with a built-in system tool. Choose a different name.";
        }
        return null;
    }

    private async Task<ToolResult> DeleteCustomToolAsync(ToolCallRequest request, CancellationToken ct)
    {
        var name = GetStringArg(request.Arguments, "name");
        if (string.IsNullOrEmpty(name)) return InvalidCall(request.Name, "Name is required");

        await messageStore.DeleteCustomToolAsync(name);
        InvalidateCustomToolsCache();
        return new ToolResult(request.Name, true, $"Custom tool '{name}' deleted.", null);
    }

    private async Task<ToolResult> ExecuteCustomToolAsync(ToolCallRequest request, CancellationToken ct)
    {
        var customTools = await messageStore.GetCustomToolsAsync();
        var tool = customTools.FirstOrDefault(t => t.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
        
        if (tool == null)
            return new ToolResult(request.Name, false, string.Empty, $"Tool '{request.Name}' not implemented.");

        return await Task.Run(async () =>
        {
            string tempDir = string.Empty;
            string tempFile = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (tool.Language == "python")
                {
                    tempFile = Path.GetTempFileName() + ".py";
                    await File.WriteAllTextAsync(tempFile, tool.ScriptContent, ct);
                    psi.FileName = "python";
                    psi.Arguments = $"\"{tempFile}\"";
                }
                else if (tool.Language == "csharp")
                {
                    tempDir = Path.Combine(Path.GetTempPath(), "KlydisCustomTool_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    // Target the SAME TFM as the host app (net10.0). A net9.0 console app fails to
                    // run on machines with only the .NET 10 runtime installed (no major roll-forward
                    // by default), so custom C# tools silently failed at runtime.
                    var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>";
                    await File.WriteAllTextAsync(Path.Combine(tempDir, "Tool.csproj"), csproj, ct);

                    var code = tool.ScriptContent;
                    // If no namespace/class is defined, wrap it in a top-level statement or just use it if they wrote one.
                    // We'll assume the model writes a valid Program.cs
                    await File.WriteAllTextAsync(Path.Combine(tempDir, "Program.cs"), code, ct);

                    psi.FileName = "dotnet";
                    psi.Arguments = $"run --project \"{tempDir}\" --nologo";
                }
                else // powershell
                {
                    tempFile = Path.GetTempFileName() + ".ps1";
                    await File.WriteAllTextAsync(tempFile, tool.ScriptContent, ct);
                    psi.FileName = "powershell.exe";
                    psi.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempFile}\"";
                }

                foreach (var arg in request.Arguments)
                {
                    if (arg.Value == null) continue;
                    // Env var names must not contain '=' or NUL; skip malformed keys instead of
                    // failing the whole tool with a confusing ArgumentException.
                    if (string.IsNullOrEmpty(arg.Key) || arg.Key.Contains('=') || arg.Key.Contains('\0'))
                        continue;

                    var stringVal = arg.Value.ToString() ?? "";
                    if (arg.Value is JsonElement jsonElement)
                    {
                        if (jsonElement.ValueKind == JsonValueKind.String) stringVal = jsonElement.GetString() ?? "";
                        else stringVal = jsonElement.GetRawText();
                    }
                    psi.EnvironmentVariables[arg.Key] = stringVal;
                }

                using var process = Process.Start(psi);
                if (process == null) return new ToolResult(request.Name, false, "", "Failed to start process");

                // Read stdout/stderr CONCURRENTLY with process execution. Reading after
                // WaitForExit deadlocks once the child fills a pipe buffer (typically ~4KB) —
                // custom tools that print more than a few KB of output were killed at the 120s
                // timeout every time. This is the same pattern RunCommandAsync uses.
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);

                using var timeoutCts = new CancellationTokenSource(120000); // 2 min to allow for dotnet run compilation
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    if (timeoutCts.IsCancellationRequested)
                    {
                        return new ToolResult(request.Name, false, "", "Custom tool timed out after 120 seconds");
                    }
                    throw;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                try
                {
                    if (!string.IsNullOrEmpty(tempFile)) File.Delete(tempFile);
                    if (!string.IsNullOrEmpty(tempDir)) Directory.Delete(tempDir, true);
                } catch { /* ignore */ }

                var output = stdout;
                if (!string.IsNullOrEmpty(stderr)) output += $"\nSTDERR:\n{stderr}";

                return new ToolResult(request.Name, process.ExitCode == 0, output, process.ExitCode != 0 ? "Tool returned non-zero exit code" : null);
            }
            catch (Exception ex)
            {
                try
                {
                    if (!string.IsNullOrEmpty(tempFile)) File.Delete(tempFile);
                    if (!string.IsNullOrEmpty(tempDir)) Directory.Delete(tempDir, true);
                } catch { /* ignore */ }
                return new ToolResult(request.Name, false, "", ex.Message);
            }
        }, ct);
    }

    private Task<ToolResult> ListSkillsAsync(ToolCallRequest request)
    {
        if (SkillLibraryManager == null)
            return Task.FromResult(new ToolResult(request.Name, false, string.Empty, "SkillLibraryManager is not configured."));

        string? category = GetStringArg(request.Arguments, "category");
        var skills = SkillLibraryManager.GetAllSkills();
        if (!string.IsNullOrWhiteSpace(category))
        {
            skills = skills.Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Skill Library Brain ({skills.Count} skills found):");
        foreach (var s in skills)
        {
            sb.AppendLine($"- [{s.Category}] {s.Name} (ID: `{s.Id}`) - {s.Description} [Enabled: {s.IsEnabled}, Source: {s.Source}]");
        }
        return Task.FromResult(new ToolResult(request.Name, true, sb.ToString().Trim(), null));
    }

    private Task<ToolResult> SearchSkillsAsync(ToolCallRequest request)
    {
        if (SkillLibraryManager == null)
            return Task.FromResult(new ToolResult(request.Name, false, string.Empty, "SkillLibraryManager is not configured."));

        string query = GetStringArg(request.Arguments, "query") ?? string.Empty;
        var matches = SkillLibraryManager.SearchSkills(query, topN: 8);

        if (matches.Count == 0)
        {
            return Task.FromResult(new ToolResult(request.Name, true, $"No skills matched query '{query}'.", null));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {matches.Count} skills matching '{query}':");
        foreach (var s in matches)
        {
            sb.AppendLine($"• **{s.Name}** (`{s.Id}`) [{s.Category}] - {s.Description}");
        }
        return Task.FromResult(new ToolResult(request.Name, true, sb.ToString().Trim(), null));
    }

    private Task<ToolResult> GetSkillDetailsAsync(ToolCallRequest request)
    {
        if (SkillLibraryManager == null)
            return Task.FromResult(new ToolResult(request.Name, false, string.Empty, "SkillLibraryManager is not configured."));

        string id = GetStringArg(request.Arguments, "skill_id") ?? string.Empty;
        var skill = SkillLibraryManager.GetSkillById(id);

        if (skill == null)
        {
            return Task.FromResult(new ToolResult(request.Name, false, string.Empty, $"Skill with ID '{id}' not found."));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"--- SKILL: {skill.Name} (ID: `{skill.Id}`) ---");
        sb.AppendLine($"Category: {skill.Category}");
        sb.AppendLine($"Source: {skill.Source}");
        sb.AppendLine($"Description: {skill.Description}");
        sb.AppendLine($"Tags: {string.Join(", ", skill.Tags)}");
        sb.AppendLine("\nDirectives / Instruction:");
        sb.AppendLine(skill.PromptInstruction);

        return Task.FromResult(new ToolResult(request.Name, true, sb.ToString().Trim(), null));
    }

    private Task<ToolResult> ActivateSkillAsync(ToolCallRequest request)
    {
        if (SkillLibraryManager == null)
            return Task.FromResult(new ToolResult(request.Name, false, string.Empty, "SkillLibraryManager is not configured."));

        string id = GetStringArg(request.Arguments, "skill_id") ?? string.Empty;
        var skill = SkillLibraryManager.GetSkillById(id);

        if (skill == null)
        {
            return Task.FromResult(new ToolResult(request.Name, false, string.Empty, $"Skill with ID '{id}' not found."));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[SKILL ACTIVATED: {skill.Name}]");
        sb.AppendLine("Specialized Domain Directives:");
        sb.AppendLine(skill.PromptInstruction.Trim());

        return Task.FromResult(new ToolResult(request.Name, true, sb.ToString(), null));
    }

    private async Task<ToolResult> LearnSkillAsync(ToolCallRequest request)
    {
        if (SkillLibraryManager == null)
            return new ToolResult(request.Name, false, string.Empty, "SkillLibraryManager is not configured.");

        string name = GetStringArg(request.Arguments, "name") ?? string.Empty;
        string description = GetStringArg(request.Arguments, "description") ?? string.Empty;
        string category = GetStringArg(request.Arguments, "category") ?? "Custom";
        string promptInstruction = GetStringArg(request.Arguments, "prompt_instruction") ?? string.Empty;
        string tagsStr = GetStringArg(request.Arguments, "tags") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(promptInstruction))
        {
            return InvalidCall(request.Name, "Both 'name' and 'prompt_instruction' are required to learn a skill.");
        }

        string id = name.Trim().ToLowerInvariant().Replace(" ", "-");
        var tags = tagsStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();

        var skill = new Klydis.Core.Skills.Skill
        {
            Id = id,
            Name = name,
            Description = description,
            Category = category,
            PromptInstruction = promptInstruction,
            Tags = tags,
            Source = "Custom",
            IsEnabled = true,
            Author = "Klydis AI",
            Version = "1.0.0"
        };

        await SkillLibraryManager.SaveSkillAsync(skill);

        return new ToolResult(request.Name, true, $"Successfully learned and registered skill '{skill.Name}' (ID: `{skill.Id}`) in category '{skill.Category}'. File saved to custom skills library.", null);
    }
    private async Task<ToolResult> DeleteSkillAsync(ToolCallRequest request)
    {
        if (SkillLibraryManager == null)
            return new ToolResult(request.Name, false, string.Empty, "SkillLibraryManager is not configured.");

        string id = GetStringArg(request.Arguments, "skill_id") ?? string.Empty;
        await SkillLibraryManager.DeleteCustomSkillAsync(id);

        return new ToolResult(request.Name, true, $"Deleted custom skill '{id}' from Skill Library.", null);
    }

    private async Task<ToolResult> SearchRagAsync(ToolCallRequest request, CancellationToken ct)
    {
        if (HybridRetriever == null)
        {
            return new ToolResult(request.Name, false, string.Empty, "RAG HybridRetriever service is not configured.");
        }

        string query = GetStringArg(request.Arguments, "query") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult(request.Name, false, string.Empty, "Search query is required.");
        }

        int topK = 5;
        if (request.Arguments != null && request.Arguments.TryGetValue("top_k", out var tkObj))
        {
            var unwrapped = UnwrapJsonElement(tkObj);
            if (unwrapped != null && int.TryParse(unwrapped.ToString(), out int k) && k > 0)
            {
                topK = Math.Clamp(k, 1, 20);
            }
        }

        string? collectionId = GetStringArg(request.Arguments, "collection_id");

        try
        {
            var results = await HybridRetriever.SearchAsync(query, topK, collectionIdFilter: collectionId, cancellationToken: ct);
            if (results.Count == 0)
            {
                return new ToolResult(request.Name, true, "No matching context chunks found in RAG index for query: " + query, null);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"RAG Hybrid Search Results ({results.Count} matches for '{query}'):\n");

            foreach (var res in results)
            {
                sb.AppendLine($"--- Source: {res.Chunk.FileTitle} | Path: {res.Chunk.SourcePath} | Collection: {res.Chunk.CollectionId} (RRF Score: {res.RrfScore:F3}) ---");
                sb.AppendLine(res.Chunk.Content);
                sb.AppendLine("--------------------------------------------------\n");
            }

            return new ToolResult(request.Name, true, sb.ToString().TrimEnd(), null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing RAG search for query '{Query}'", query);
            return new ToolResult(request.Name, false, string.Empty, ex.Message);
        }
    }

    private async Task<ToolResult> ListRagCollectionsAsync(CancellationToken ct)
    {
        if (VectorStore == null)
        {
            return new ToolResult("list_rag_collections", false, string.Empty, "VectorStore service is not configured.");
        }

        try
        {
            await VectorStore.InitializeAsync();
            var collections = await VectorStore.GetCollectionsAsync();
            int totalChunks = VectorStore.GetTotalChunkCount();

            if (collections.Count == 0)
            {
                return new ToolResult("list_rag_collections", true, "No project workspace folders have been indexed in the RAG vector store yet. Use tool 'index_folder_rag' to index a project folder.", null);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Indexed RAG Workspace Collections ({collections.Count} collections, {totalChunks} total vector chunks):\n");

            foreach (var col in collections)
            {
                sb.AppendLine($"- Collection ID: `{col.Id}` | Name: \"{col.Name}\" | Path: {col.FolderPath} | Indexed: {col.CreatedAt:yyyy-MM-dd HH:mm}");
            }

            return new ToolResult("list_rag_collections", true, sb.ToString().TrimEnd(), null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing RAG collections");
            return new ToolResult("list_rag_collections", false, string.Empty, ex.Message);
        }
    }

    private async Task<ToolResult> IndexFolderRagAsync(ToolCallRequest request, CancellationToken ct)
    {
        if (VectorStore == null || IngestionEngine == null)
        {
            return new ToolResult(request.Name, false, string.Empty, "VectorStore or DocumentIngestionEngine service is not configured.");
        }

        string folderPath = GetStringArg(request.Arguments, "folder_path") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return new ToolResult(request.Name, false, string.Empty, $"Directory path does not exist or is invalid: '{folderPath}'");
        }

        string collectionName = GetStringArg(request.Arguments, "collection_name") ?? Path.GetFileName(folderPath);
        if (string.IsNullOrWhiteSpace(collectionName)) collectionName = Path.GetFileName(folderPath);

        try
        {
            await VectorStore.InitializeAsync();
            var collection = await VectorStore.AddOrUpdateCollectionAsync(
                name: collectionName,
                folderPath: folderPath,
                embeddingModel: "LLamaEmbedder-Local",
                dimension: 384
            );
            int chunksCreated = await IngestionEngine.IndexDirectoryAsync(
                collectionId: collection.Id,
                directoryPath: folderPath,
                cancellationToken: ct
            );

            string resultMsg = $"Successfully indexed folder '{folderPath}' into collection '{collectionName}' (Collection ID: `{collection.Id}`). Created {chunksCreated} vector chunks.";
            return new ToolResult(request.Name, true, resultMsg, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error indexing folder for RAG");
            return new ToolResult(request.Name, false, string.Empty, ex.Message);
        }
    }

    private async Task<ToolResult> LearnLessonAsync(ToolCallRequest request, CancellationToken ct, string? modelPath)
    {
        if (AdaptiveLearning == null)
        {
            return new ToolResult(request.Name, false, string.Empty, "Adaptive learning service is not configured.");
        }

        var lesson = GetStringArg(request.Arguments, "lesson");
        if (string.IsNullOrWhiteSpace(lesson))
        {
            return new ToolResult(request.Name, false, string.Empty, "Lesson content is required.");
        }
        if (lesson.Length > 2000)
        {
            lesson = lesson[..2000];
        }

        var category = GetStringArg(request.Arguments, "category") ?? "general";
        await AdaptiveLearning.RecordLessonAsync(
            modelPath,
            Klydis.Core.Learning.AdaptiveLearningService.TypeExplicit,
            lesson,
            source: $"explicit:{category.Trim()}",
            ct: ct);

        return new ToolResult(request.Name, true, $"Lesson recorded for future sessions: {lesson}", null);
    }

    private async Task<ToolResult> RecallLessonsAsync(ToolCallRequest request, CancellationToken ct, string? modelPath)
    {
        if (AdaptiveLearning == null)
        {
            return new ToolResult(request.Name, false, string.Empty, "Adaptive learning service is not configured.");
        }

        int limit = 8;
        if (request.Arguments != null && request.Arguments.TryGetValue("limit", out var limObj))
        {
            var unwrapped = UnwrapJsonElement(limObj);
            if (unwrapped != null && int.TryParse(unwrapped.ToString(), out int l) && l > 0)
            {
                limit = Math.Clamp(l, 1, 20);
            }
        }

        var text = await AdaptiveLearning.RecallLessonsTextAsync(modelPath, limit, ct);
        return new ToolResult(request.Name, true, text, null);
    }

    private static ToolResult ExecuteTaskComplete(ToolCallRequest request)
    {
        string summary = GetStringArg(request.Arguments, "summary") ?? "Goal marked as complete.";
        return new ToolResult("task_complete", true, $"[GOAL COMPLETED] Summary: {summary}", null);
    }

    private static ToolResult ExecuteTaskProgress(ToolCallRequest request)
    {
        string percentStr = GetStringArg(request.Arguments, "percent") ?? "0";
        int.TryParse(percentStr, out int pct);
        string status = GetStringArg(request.Arguments, "status") ?? "In progress";
        return new ToolResult("task_progress", true, $"[PROGRESS UPDATE: {pct}%] Status: {status}", null);
    }

    // ---- Agent task plan / todo list (keyed by session) ----
    // The model maintains its todo list through the 'plan' tool; ChatEngine re-injects the
    // plan into the prompt on every iteration, closing the goal-execution feedback loop.
    // The plan is PERSISTED to the session store on every mutation (SaveSessionPlanAsync) and
    // lazily restored on first access after a restart, so a long-horizon task's todo list
    // survives app restarts and model switches instead of starting from zero.
    private sealed record PlanTask(string Text, bool Done);
    private sealed record PlanSnapshot(List<PlanTask>? Items, int Progress, string? OwnerUserMessage = null);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<PlanTask>> _sessionPlans = new();
    // P1: plan lists are mutated by tool executions and read by the UI (every 2s) and by
    // prompt builds — one lock guards every mutation and snapshot read.
    private readonly object _sessionPlanLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _sessionPlanProgress = new();
    // Which user message the plan belongs to (its text, matching ChatEngine's convention for
    // identifying the current user message). The plan is scoped to the TASK in that message:
    // when a LATER user message starts a new task in the same chat, the harness must stop
    // presenting the old checklist as active work (the observed contamination: the model kept
    // executing the previous task's plan items after the user moved on). An unset owner (null)
    // means "no task boundary recorded" and is treated as current-task state, so legacy plans
    // behave exactly as before.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessionPlanOwner = new();
    // Sessions already hydrated from the store, so the lazy restore hits the DB at most once
    // per session per process (the getters below are called on the UI thread every 2s).
    private readonly HashSet<string> _planLoadAttempted = new();

    /// <summary>
    /// The user message whose turn is currently executing. Set by ChatEngine at the start of
    /// each StreamResponseAsync call and cleared when the turn ends. When the model mutates
    /// the session plan mid-turn, this text is stamped as the plan's owner — the durable
    /// task boundary that lets the harness distinguish a live checklist from an obsolete one
    /// left over from an earlier task in the same chat. Null outside a turn (direct tool
    /// invocations, tests), in which case no owner is recorded.
    /// </summary>
    public string? CurrentTaskUserMessage { get; set; }

    /// <summary>
    /// The text of the user message the session's plan belongs to, or null when the plan has
    /// no recorded owner (legacy plans, or plans never mutated inside a turn). Used by
    /// ChatEngine to decide whether the checklist is the CURRENT task's plan or a previous
    /// task's — a plan whose owner is not the current user message must not be injected as
    /// active work with a continuation contract.
    /// </summary>
    public string? GetSessionPlanOwner(string sessionId)
    {
        EnsureSessionPlanLoaded(sessionId);
        return string.IsNullOrEmpty(sessionId) || !_sessionPlanOwner.TryGetValue(sessionId, out var owner)
            ? null
            : owner;
    }

    /// <summary>
    /// The harness-owned initial planner: establishes a baseline scaffold plan for a session
    /// WITHOUT a tool call. The runtime creates the plan; the model refines or replaces it.
    /// Identical in effect to the 'plan' tool's create path — same storage, same owner
    /// stamping, same persistence — so every downstream consumer (prompt injection, PLAN tab,
    /// completion gate, stagnation tracker) sees one plan format. The scaffold is deliberately
    /// generic: it gives the task a durable backbone from the first turn; the model is told
    /// to replace it with a task-specific checklist via 'plan' (action=create) when it has
    /// one.
    /// </summary>
    public async Task SeedSessionPlanAsync(string sessionId, IReadOnlyList<string> items)
    {
        string key = sessionId ?? string.Empty;
        EnsureSessionPlanLoaded(key);
        var plan = _sessionPlans.GetOrAdd(key, _ => new List<PlanTask>());
        // P1: guard the mutation — the UI and prompt builds read this list concurrently.
        lock (_sessionPlanLock)
        {
            plan.Clear();
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    plan.Add(new PlanTask(item.Trim(), false));
                }
            }
        }
        _sessionPlanOwner[key] = CurrentTaskUserMessage ?? string.Empty;
        _sessionPlanProgress.TryRemove(key, out _);
        try
        {
            var snapshot = new PlanSnapshot(plan.ToList(), -1, GetSessionPlanOwner(key));
            string json = System.Text.Json.JsonSerializer.Serialize(snapshot);
            await messageStore.SaveSessionPlanAsync(key, json);
            if (TaskManager != null && !string.IsNullOrEmpty(CurrentTaskId))
            {
                await TaskManager.SavePlanAsync(CurrentTaskId, json);
            }
            await EmitExecutionEventAsync(sessionId, "PlanCreated", CurrentTaskId, null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist harness-seeded plan for {SessionId}.", sessionId);
        }
        logger.LogInformation("Harness seeded initial plan ({Count} items) for session {SessionId}.", plan.Count, sessionId);
    }

    /// <summary>
    /// Programmatically marks a plan item as completed by step text or index and persists the updated plan.
    /// </summary>
    public async Task AdvancePlanItemDoneAsync(string sessionId, string stepTitle)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(stepTitle)) return;
        EnsureSessionPlanLoaded(sessionId);
        if (!_sessionPlans.TryGetValue(sessionId, out var plan)) return;

        bool updated = false;
        lock (_sessionPlanLock)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                if (!plan[i].Done && (plan[i].Text.Equals(stepTitle, StringComparison.OrdinalIgnoreCase) ||
                                      stepTitle.Contains(plan[i].Text, StringComparison.OrdinalIgnoreCase) ||
                                      plan[i].Text.Contains(stepTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    plan[i] = plan[i] with { Done = true };
                    updated = true;
                    break;
                }
            }
        }

        if (updated)
        {
            try
            {
                var snapshot = new PlanSnapshot(plan.ToList(), -1, GetSessionPlanOwner(sessionId));
                string json = System.Text.Json.JsonSerializer.Serialize(snapshot);
                await messageStore.SaveSessionPlanAsync(sessionId, json).ConfigureAwait(false);
                if (TaskManager != null && !string.IsNullOrEmpty(CurrentTaskId))
                {
                    await TaskManager.SavePlanAsync(CurrentTaskId, json).ConfigureAwait(false);
                }
                await EmitExecutionEventAsync(sessionId, "PlanUpdated", CurrentTaskId, null, null).ConfigureAwait(false);
                logger.LogInformation("Autonomous loop advanced plan item '{Title}' to completed for session {SessionId}.", stepTitle, sessionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist updated plan after advancing item for session {SessionId}.", sessionId);
            }
        }
    }

    /// <summary>
    /// Hydrates a session's persisted plan (if any) into the in-memory dictionaries. Called
    /// lazily from the plan getters and from every 'plan' tool execution, so a session opened
    /// after an app restart shows the plan the model left behind. Best-effort: a failed load
    /// is logged and the session simply starts with an empty plan.
    /// </summary>
    private void EnsureSessionPlanLoaded(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_planLoadAttempted)
        {
            if (!_planLoadAttempted.Add(sessionId)) return;
        }
        try
        {
            var record = messageStore.GetSessionAsync(sessionId).GetAwaiter().GetResult();
            if (record?.PlanJson != null)
            {
                var snapshot = System.Text.Json.JsonSerializer.Deserialize<PlanSnapshot>(record.PlanJson);
                if (snapshot != null && snapshot.Items != null)
                {
                    lock (_sessionPlanLock)
                    {
                        _sessionPlans[sessionId] = snapshot.Items;
                    }
                    if (snapshot.Progress >= 0)
                    {
                        _sessionPlanProgress[sessionId] = Math.Clamp(snapshot.Progress, 0, 100);
                    }
                    if (!string.IsNullOrEmpty(snapshot.OwnerUserMessage))
                    {
                        _sessionPlanOwner[sessionId] = snapshot.OwnerUserMessage;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load persisted plan for session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// Resets the session's in-memory plan state and re-hydrates from the persisted
    /// <c>sessions.plan_json</c>. Called by ChatEngine after a task switch: the old task's
    /// plan is already mirrored to its record, <c>sessions.plan_json</c> is set to the new
    /// task's plan (or cleared), and this re-arms the live checklist so the model never sees
    /// the previous task's items.
    /// </summary>
    public void ResetSessionPlanState(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_planLoadAttempted)
        {
            _planLoadAttempted.Remove(sessionId);
        }
        _sessionPlans.TryRemove(sessionId, out _);
        _sessionPlanProgress.TryRemove(sessionId, out _);
        _sessionPlanOwner.TryRemove(sessionId, out _);
        EnsureSessionPlanLoaded(sessionId);
    }

    /// <summary>
    /// A single item in the agent's task plan (todo list).
    /// </summary>
    public sealed record PlanEntry(string Text, bool Done);

    /// <summary>
    /// Returns the raw todo list for a session, or an empty list when the agent has not
    /// established a plan yet. Used by the UI (right-side Plan tab) and by ChatEngine.
    /// </summary>
    public IReadOnlyList<PlanEntry> GetSessionPlanEntries(string sessionId)
    {
        EnsureSessionPlanLoaded(sessionId);
        if (string.IsNullOrEmpty(sessionId) || !_sessionPlans.TryGetValue(sessionId, out var plan) || plan.Count == 0)
        {
            return Array.Empty<PlanEntry>();
        }
        lock (_sessionPlanLock)
        {
            return plan.Select(t => new PlanEntry(t.Text, t.Done)).ToList();
        }
    }

    /// <summary>
    /// Distinct file paths this session has produced via write_file/str_replace, oldest first.
    /// Surfaces the workbench PREVIEW-tab contents into the model's context so it knows which
    /// of its deliverables the user can view live.
    /// </summary>
    public IReadOnlyList<string> GetSessionArtifactPaths(string sessionId)
    {
        EnsureSessionToolActivityLoaded(sessionId);
        if (string.IsNullOrEmpty(sessionId) || !_sessionToolActivity.TryGetValue(sessionId, out var list) || list.Count == 0)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_toolActivityLock)
        {
            foreach (var r in list)
            {
                if (r.ToolName is not ("write_file" or "str_replace" or "edit_file")) continue;
                string p = ExtractPathArg(r.ArgsJson);
                if (string.IsNullOrEmpty(p) || !seen.Add(p)) continue;
                paths.Add(p);
            }
        }
        return paths;
    }

    private static string ExtractPathArg(string argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return string.Empty;
            if (root.TryGetProperty("path", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return p.GetString() ?? string.Empty;
            }
        }
        catch { /* best effort */ }
        return string.Empty;
    }

    /// <summary>
    /// Returns the formatted todo list for a session ("1. [x] task" lines), or an empty list
    /// when the agent has not established a plan yet. Called by ChatEngine each prompt build.
    /// </summary>
    public IReadOnlyList<string> GetSessionPlan(string sessionId)
    {
        EnsureSessionPlanLoaded(sessionId);
        if (string.IsNullOrEmpty(sessionId) || !_sessionPlans.TryGetValue(sessionId, out var plan) || plan.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>(plan.Count);
        lock (_sessionPlanLock)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                lines.Add($"{i + 1}. {(plan[i].Done ? "[x]" : "[ ]")} {plan[i].Text}");
            }
        }
        return lines;
    }

    /// <summary>
    /// Returns the last reported overall completion percent for a session, or -1 when none.
    /// </summary>
    public int GetSessionPlanProgress(string sessionId)
    {
        EnsureSessionPlanLoaded(sessionId);
        return string.IsNullOrEmpty(sessionId) || !_sessionPlanProgress.TryGetValue(sessionId, out var p) ? -1 : p;
    }

    // ---- Per-session tool activity ----
    // Records every tool invocation (name + serialized args + time) keyed by session, so the
    // UI's right-side panel can show ONLY what this chat actually did — files it read/wrote,
    // artifacts it produced — instead of workspace-global git state. The list is hydrated
    // from the durable tool_activity table on first access and appended on every call, so it
    // is a CACHE of SQLite, not the source of truth — activity survives restarts/switches.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<ToolActivityRecord>> _sessionToolActivity = new();
    // P1: the per-session activity list is appended from tool-completion threads while the
    // UI polls it every 2s — one lock guards every mutation and every snapshot read.
    private readonly object _toolActivityLock = new();
    // Sessions already hydrated from the durable tool_activity table (the getters below are
    // called by the UI every 2s, so the lazy restore must hit the DB at most once per session).
    private readonly HashSet<string> _toolActivityLoadAttempted = new();

    /// <summary>
    /// Hydrates a session's persisted tool activity (if any) into the in-memory cache. Called
    /// lazily from the activity getters, so a session opened after an app restart shows the
    /// tool history the model left behind. Best-effort: a failed load is logged and the
    /// session simply starts with an empty (live) list.
    /// </summary>
    private void EnsureSessionToolActivityLoaded(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_toolActivityLoadAttempted)
        {
            if (!_toolActivityLoadAttempted.Add(sessionId)) return;
        }
        try
        {
            var rows = messageStore.GetToolActivityBySessionAsync(sessionId).GetAwaiter().GetResult();
            if (rows.Count == 0) return;
            var list = _sessionToolActivity.GetOrAdd(sessionId, _ => new List<ToolActivityRecord>());
            lock (_toolActivityLock)
            {
                foreach (var r in rows)
                {
                    list.Add(new ToolActivityRecord(r.ToolName, r.ArgsJson, r.Success, r.OutputPreview, r.TimestampUtc.ToLocalTime()));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load persisted tool activity for session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// Returns the recorded tool invocations for a session, oldest first. Durable: hydrated
    /// from SQLite on first access, so the Files/Preview/Terminal panels survive restarts.
    /// </summary>
    public IReadOnlyList<ToolActivityRecord> GetSessionToolActivity(string sessionId)
    {
        EnsureSessionToolActivityLoaded(sessionId);
        if (string.IsNullOrEmpty(sessionId) || !_sessionToolActivity.TryGetValue(sessionId, out var list) || list.Count == 0)
        {
            return Array.Empty<ToolActivityRecord>();
        }
        // P1: never hand out the live list (a tool completing concurrently would mutate it
        // while the UI renders) — return an immutable snapshot.
        lock (_toolActivityLock)
        {
            return list.ToArray();
        }
    }

    /// <summary>
    /// Clears the recorded tool activity for a session (e.g. when the chat is deleted).
    /// </summary>
    public void ClearSessionToolActivity(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            EnsureSessionToolActivityLoaded(sessionId);
            _sessionToolActivity.TryRemove(sessionId, out _);
        }
    }

    // ---- Identical-failed-call retry guard ----
    // Models sometimes repeat a failing tool call with the exact same arguments (observed: a
    // broken list_directory path retried 6+ times, burning turns and context). Successful
    // calls are never affected — repeated successful calls like check_message_queue polling
    // with identical empty args are legitimate. Consecutive identical FAILURES escalate:
    // the 2nd appends a warning, the 3rd+ is blocked before execution.
    private System.Collections.Concurrent.ConcurrentDictionary<string, (string Key, int FailCount)>? _lastFailedCalls;

    /// <summary>
    /// True when the session's most recent calls for this key have failed 3+ consecutive times
    /// with identical arguments — the call should be blocked before it executes again.
    /// </summary>
    private bool CheckIdenticalRetry(string sessionId, string key, out string blockMessage)
    {
        blockMessage = string.Empty;
        _lastFailedCalls ??= new System.Collections.Concurrent.ConcurrentDictionary<string, (string Key, int FailCount)>();
        if (!_lastFailedCalls.TryGetValue(sessionId ?? string.Empty, out var cur) || cur.Key != key || cur.FailCount < 3)
        {
            return false;
        }

        string toolName = key.Substring(0, key.IndexOf('|') >= 0 ? key.IndexOf('|') : key.Length);
        blockMessage = $"BLOCKED: '{toolName}' has failed {cur.FailCount} consecutive times with IDENTICAL arguments. " +
                       "Repeating the same call cannot succeed. Re-read the tool's description in the schema, " +
                       "change your arguments, or use a different tool. Further identical retries will be refused.";
        return true;
    }

    /// <summary>
    /// Records the outcome of a tool call for duplicate tracking. Returns (Status, Message)
    /// where Status 0 = normal, 1 = append the warning to the result, 2 = append the blocked
    /// message (the NEXT identical call will be refused by <see cref="CheckIdenticalRetry"/>).
    /// </summary>
    private (int Status, string Message) TrackIdenticalCallOutcome(string sessionId, string key, bool succeeded)
    {
        _lastFailedCalls ??= new System.Collections.Concurrent.ConcurrentDictionary<string, (string Key, int FailCount)>();
        var map = _lastFailedCalls;
        string sid = sessionId ?? string.Empty;

        if (succeeded)
        {
            map[sid] = (key, 0);
            return (0, string.Empty);
        }

        var cur = map.TryGetValue(sid, out var c) ? c : (Key: string.Empty, FailCount: 0);
        int failCount = cur.Key == key ? cur.FailCount + 1 : 1;
        map[sid] = (key, failCount);

        string toolName = key.Substring(0, key.IndexOf('|') >= 0 ? key.IndexOf('|') : key.Length);
        if (failCount >= 3)
        {
            return (2, $"'{toolName}' has now failed {failCount} consecutive times with IDENTICAL arguments. STOP retrying — " +
                       "re-read the tool description, change the arguments, or use a different tool. " +
                       "The next identical call will be blocked before execution.");
        }
        if (failCount == 2)
        {
            return (1, $"Warning: '{toolName}' failed again with identical arguments (2nd consecutive attempt). " +
                       "Do NOT retry the same arguments — change them or use a different tool.");
        }
        return (0, string.Empty);
    }

    private async Task<ToolResult> ExecutePlanAsync(ToolCallRequest request, string sessionId)
    {
        string key = sessionId ?? string.Empty;
        EnsureSessionPlanLoaded(key);
        var plan = _sessionPlans.GetOrAdd(key, _ => new List<PlanTask>());

        string? rawAction = GetStringArg(request.Arguments, "action");
        string action;
        if (string.IsNullOrWhiteSpace(rawAction))
        {
            if (!string.IsNullOrWhiteSpace(GetStringArg(request.Arguments, "items")))
                action = plan.Count == 0 ? "create" : "add";
            else if (!string.IsNullOrWhiteSpace(GetStringArg(request.Arguments, "item")))
                action = "complete";
            else
                action = "show";
        }
        else
        {
            action = rawAction.Trim().ToLowerInvariant();
        }

        // P1: the plan list is read concurrently by the UI and prompt builds; mutations and
        // the persistence snapshot are taken under the lock. The SQLite await happens AFTER
        // the lock is released.
        bool planMutated = false;
        PlanSnapshot? snapshot = null;
        lock (_sessionPlanLock)
        {
        switch (action)
        {
            case "create":
                plan.Clear();
                goto case "add";
            case "add":
                foreach (var line in SplitPlanItems(GetStringArg(request.Arguments, "items")))
                {
                    plan.Add(new PlanTask(line, false));
                }
                planMutated = true;
                break;
            case "complete":
                MarkPlanItems(request, plan, done: true);
                planMutated = true;
                break;
            case "remove":
                MarkPlanItems(request, plan, done: false, remove: true);
                planMutated = true;
                break;
            case "clear":
                plan.Clear();
                _sessionPlanProgress.TryRemove(sessionId ?? string.Empty, out _);
                planMutated = true;
                break;
            case "show":
            default:
                break;
        }

        // The plan now belongs to the task in the user message currently executing. Stamped on
        // every mutation (including checking off an item), so a plan the model re-engages on a
        // later message is adopted as that message's task plan — and one it never touches again
        // stays bound to the message that created it.
        if (planMutated)
        {
            _sessionPlanOwner[key] = CurrentTaskUserMessage ?? string.Empty;
        }

        if (request.Arguments != null && request.Arguments.TryGetValue("progress", out var progObj))
        {
            var raw = ToolExecutor.UnwrapJsonElement(progObj)?.ToString();
            if (int.TryParse(raw, out int pct))
            {
                _sessionPlanProgress[key] = Math.Clamp(pct, 0, 100);
            }
        }

        snapshot = new PlanSnapshot(plan.ToList(), GetSessionPlanProgress(key), GetSessionPlanOwner(key));
        }

        // Checkpoint: persist the plan + progress + owner so the todo list (and its task
        // boundary) survives restarts and model switches (long-horizon tasks keep their
        // step-level plan across sessions). Mirrored to the current task's record so the
        // checklist follows the TASK — switching to a new task never inherits it, and
        // reopening a task restores its plan.
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(snapshot!);
            await messageStore.SaveSessionPlanAsync(key, json);
            if (TaskManager != null && !string.IsNullOrEmpty(CurrentTaskId))
            {
                await TaskManager.SavePlanAsync(CurrentTaskId, json);
            }
            await EmitExecutionEventAsync(sessionId, "PlanUpdated", CurrentTaskId, null, null);
        }
        catch (Exception ex)
        {
            // P0: the tool result must NOT report success when the durable plan write failed.
            // "Model believes plan changed; memory believes plan changed; database still holds
            // the old plan" is a divergence recovery would silently undo. Rethrow — the
            // executor converts this into a FAILED tool result, so the model sees the write
            // did not commit and the durable state machine stays authoritative.
            logger.LogError(ex, "Failed to persist session plan for {SessionId}; the plan change was NOT durable — the plan tool call is failed.", sessionId);
            throw;
        }

        var formatted = GetSessionPlan(key);
        string body = formatted.Count > 0 ? string.Join("\n", formatted) : "(plan is empty)";
        int progress = GetSessionPlanProgress(sessionId ?? string.Empty);
        string progressLine = progress >= 0 ? $"\nOverall progress: {progress}%" : string.Empty;
        return new ToolResult("plan", true, $"[PLAN {action.ToUpperInvariant()} — current todo list:]\n{body}{progressLine}", null);
    }

    private static void MarkPlanItems(ToolCallRequest request, List<PlanTask> plan, bool done, bool remove = false)
    {
        string item = (GetStringArg(request.Arguments, "item") ?? string.Empty).Trim();
        if (item.Length == 0) return;

        // Match by number first ("2" or "2. rest of text"), then by text containment.
        bool TryNumber(string s, out int idx)
        {
            idx = -1;
            string head = s;
            int dot = s.IndexOfAny(new[] { '.', ')' });
            if (dot > 0) head = s.Substring(0, dot);
            return int.TryParse(head.Trim(), out idx) && idx >= 1 && idx <= plan.Count;
        }

        if (TryNumber(item, out int numIdx))
        {
            var t = plan[numIdx - 1];
            if (remove) plan.RemoveAt(numIdx - 1);
            else plan[numIdx - 1] = t with { Done = done };
            return;
        }

        for (int i = plan.Count - 1; i >= 0; i--)
        {
            if (plan[i].Text.Contains(item, StringComparison.OrdinalIgnoreCase))
            {
                if (remove) plan.RemoveAt(i);
                else plan[i] = plan[i] with { Done = done };
                break;
            }
        }
    }

    private static IEnumerable<string> SplitPlanItems(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Enumerable.Empty<string>();
        return raw.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
    }
}
