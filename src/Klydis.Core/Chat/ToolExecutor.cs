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
using System.Net.Http;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System.Management;
using System.IO;

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
public record ToolResult(string ToolName, bool Success, string Output, string? Error);

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
    Klydis.Core.RAG.DocumentIngestionEngine? ingestionEngine = null)
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly StealthBrowserService? _stealthBrowserService = stealthBrowserService;
    
    public ModelMessageQueue? MessageQueue { get; set; } = messageQueue;
    public Klydis.Core.Skills.SkillLibraryManager? SkillLibraryManager { get; set; } = skillLibraryManager;
    public Klydis.Core.RAG.VectorStore? VectorStore { get; set; } = vectorStore;
    public Klydis.Core.RAG.HybridRetriever? HybridRetriever { get; set; } = hybridRetriever;
    public Klydis.Core.RAG.DocumentIngestionEngine? IngestionEngine { get; set; } = ingestionEngine;

    /// <summary>
    /// Gets or sets the current risk level mode.
    /// </summary>
    public RiskLevel CurrentRiskLevel { get; set; } = RiskLevel.Standard;

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
        new ToolDefinition("crawl_url", "Fetches and renders a web page, extracts main content as clean Markdown. Use for reading specific documentation pages or articles.", new List<ToolParameter>
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
        new ToolDefinition("task_complete", "Signals that the current user goal or multi-step task has been fully completed. Call this ONLY when the goal is 100% accomplished. Provide a clear, detailed summary of what was completed.", new List<ToolParameter>
        {
            new("summary", "string", "Summary of what was accomplished to complete the goal", true)
        }, false),
        new ToolDefinition("task_progress", "Reports intermediate progress toward the current goal during autonomous multi-turn execution.", new List<ToolParameter>
        {
            new("percent", "integer", "Estimated percentage of goal completion (0-100)", true),
            new("status", "string", "Brief description of current progress and next steps", true)
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
        if (messageStore != null)
        {
            var customTools = await messageStore.GetCustomToolsAsync();
            
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
                allTools.Add(new ToolDefinition(ct.Name, ct.Description, parameters, RequiresApproval: false));
            }
        }

        return allTools;
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
    public async Task<ToolResult> ExecuteToolAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        logger.LogInformation("Executing tool: {ToolName}", request.Name);
        var tools = await GetToolDefinitionsAsync();
        var toolDef = tools.FirstOrDefault(t => t.Name == request.Name);
        
        if (toolDef == null)
        {
            var validToolNames = string.Join(", ", tools.Select(t => t.Name));
            string commandHint = (request.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("terminal", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
                                  request.Name.Equals("exec", StringComparison.OrdinalIgnoreCase))
                ? $"\nAction Required: You attempted to call '{request.Name}' as a tool name. To run command line commands or launch processes, call tool 'run_command' with argument {{\"CommandLine\": \"...\"}}."
                : "";

            var guidance = $"Tool '{request.Name}' does not exist in available system tools.{commandHint}\n" +
                           $"Available valid tools are: [{validToolNames}].\n" +
                           $"Guidance: Use 'run_command' for system commands, 'read_file'/'write_file' for file operations, or 'search_web'/'search_rag' for retrieval.";
            return new ToolResult(request.Name, false, string.Empty, guidance);
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

        ToolResult result;
        try
        {
            result = request.Name switch
            {
                "read_file" => await ReadFileAsync(request, ct),
                "write_file" => await WriteFileAsync(request, ct),
                "list_directory" => await ListDirectoryAsync(request, ct),
                "run_command" => await RunCommandAsync(request, ct),
                "get_system_info" => await GetSystemInfoAsync(ct),
                "search_web" => await SearchWebAsync(request, ct),
                "crawl_url" => await CrawlUrlAsync(request, ct),
                "search_files" => await SearchFilesAsync(request, ct),
                "store_memory" => await StoreMemoryAsync(request, sessionId, ct),
                "retrieve_memory" => await RetrieveMemoryAsync(request, sessionId, ct),
                "summarize_context" => await SummarizeContextAsync(request, sessionId, ct),
                "check_message_queue" => await CheckMessageQueueAsync(sessionId),
                "incorporate_queued_message" => await IncorporateQueuedMessageAsync(request, sessionId),
                "create_custom_tool" => await CreateCustomToolAsync(request, ct),
                "delete_custom_tool" => await DeleteCustomToolAsync(request, ct),
                "list_skills" => await ListSkillsAsync(request),
                "search_skills" => await SearchSkillsAsync(request),
                "get_skill_details" => await GetSkillDetailsAsync(request),
                "activate_skill" => await ActivateSkillAsync(request),
                "learn_skill" => await LearnSkillAsync(request),
                "delete_skill" => await DeleteSkillAsync(request),
                "search_rag" => await SearchRagAsync(request, ct),
                "list_rag_collections" => await ListRagCollectionsAsync(ct),
                "index_folder_rag" => await IndexFolderRagAsync(request, ct),
                "task_complete" => ExecuteTaskComplete(request),
                "task_progress" => ExecuteTaskProgress(request),
                _ => await ExecuteCustomToolAsync(request, ct)
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing tool {ToolName}", request.Name);
            result = new ToolResult(request.Name, false, string.Empty, ex.Message);
        }

        result = ProcessToolOutputOffload(result);
        ToolExecuted?.Invoke(this, result);
        return result;
    }

    private ToolResult ProcessToolOutputOffload(ToolResult result)
    {
        if (!EnableOutputOffloading || !result.Success || string.IsNullOrEmpty(result.Output))
            return result;

        if (result.Output.Length <= MaxToolOutputChars)
            return result;

        // Prevent recursive offloading loops when reading an offloaded tool output file or when content is already an offload message
        if (result.Output.StartsWith("[Tool Output Exceeded Context Budget]"))
            return result;

        try
        {
            var offloadDir = Path.Combine(Directory.GetCurrentDirectory(), ".klydis", "artifacts", "tool_outputs");
            Directory.CreateDirectory(offloadDir);

            var fileName = $"offload_{result.ToolName}_{Guid.NewGuid():N}.txt";
            var filePath = Path.Combine(offloadDir, fileName);

            File.WriteAllText(filePath, result.Output);

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
                                   $"[ACTION REQUIRED: You MUST call tool read_file with path '{filePath}' to retrieve the complete content before forming your response. Do NOT tell the user to read the file themselves — read it now and synthesize the answer.]";

            logger.LogInformation("Tool output for {ToolName} ({CharCount} chars) offloaded to {FilePath}", result.ToolName, result.Output.Length, filePath);

            return new ToolResult(result.ToolName, result.Success, offloadedMessage, result.Error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to offload tool output to disk for {ToolName}", result.ToolName);
            return result;
        }
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

    private bool IsRiskyRequest(ToolCallRequest request)
    {
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

    private async Task<ToolResult> ReadFileAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        if (string.IsNullOrEmpty(path)) return new ToolResult(request.Name, false, "", "Path is required");
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
            var lines = await File.ReadAllLinesAsync(path, ct);
            int start = (startLine ?? 1) - 1;
            if (start < 0) start = 0;
            if (start >= lines.Length) return new ToolResult(request.Name, true, "", null);

            int count = lines.Length - start;
            if (endLine.HasValue)
            {
                int end = endLine.Value;
                if (end < startLine.GetValueOrDefault(1)) end = startLine.GetValueOrDefault(1);
                count = Math.Min(count, end - start + 1);
            }

            var slicedLines = lines.Skip(start).Take(count);
            var slicedContent = string.Join("\n", slicedLines);
            return new ToolResult(request.Name, true, slicedContent, null);
        }

        var content = await File.ReadAllTextAsync(path, ct);
        return new ToolResult(request.Name, true, content, null);
    }

    private async Task<ToolResult> WriteFileAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        var content = GetStringArg(request.Arguments, "content");
        if (string.IsNullOrEmpty(path)) return new ToolResult(request.Name, false, "", "Path is required");
        if (content == null) return new ToolResult(request.Name, false, "", "Content is required");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        
        await File.WriteAllTextAsync(path, content, ct);
        return new ToolResult(request.Name, true, "File written successfully", null);
    }

    private Task<ToolResult> ListDirectoryAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        if (string.IsNullOrEmpty(path)) return Task.FromResult(new ToolResult(request.Name, false, "", "Path is required"));
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
        if (string.IsNullOrEmpty(command)) return new ToolResult(request.Name, false, "", "Command is required");

        var workingDir = GetStringArg(request.Arguments, "working_directory");
        if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
        {
            workingDir = Directory.GetCurrentDirectory();
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

            if (string.IsNullOrWhiteSpace(output) && process.ExitCode == 0)
            {
                output = "Command executed successfully with no output.";
            }

            return new ToolResult(request.Name, process.ExitCode == 0, output, process.ExitCode != 0 ? $"Command exited with code {process.ExitCode}" : null);
        }
        catch (Exception ex)
        {
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
        if (string.IsNullOrEmpty(query)) return new ToolResult(request.Name, false, "", "Query is required");

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
        if (string.IsNullOrEmpty(url)) return new ToolResult(request.Name, false, "", "URL is required");

        try
        {
            if (_stealthBrowserService != null)
            {
                logger.LogInformation("CrawlUrlAsync using StealthBrowserService for URL: {Url}", url);
                var stealthResult = await _stealthBrowserService.CrawlUrlAsync(url, ct);
                return new ToolResult(request.Name, true, stealthResult, null);
            }

            // Fallback to basic Playwright if stealth service is unavailable
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
            
            var header = $"# Page Title: {title}\nSource URL: {url}\n\n---\n\n";
            var fullOutput = header + markdown;

            if (fullOutput.Length > 20000) fullOutput = fullOutput[..20000] + "\n\n... [TRUNCATED]";

            return new ToolResult(request.Name, true, fullOutput, null);
        }
        catch (Microsoft.Playwright.PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            return new ToolResult(request.Name, false, "", "Browser binaries not found. You must run the playwright installation script first: `playwright.ps1 install` ");
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

    private async Task<ToolResult> SearchFilesAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = GetStringArg(request.Arguments, "path");
        var pattern = GetStringArg(request.Arguments, "pattern") ?? "*.*";
        var contains = GetStringArg(request.Arguments, "contains");

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return new ToolResult(request.Name, false, "", "Valid path is required");

        try
        {
            var files = Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories);
            var results = new List<string>();

            foreach (var file in files)
            {
                if (results.Count >= 20)
                {
                    results.Add("... [TRUNCATED 20+ RESULTS]");
                    break;
                }

                if (!string.IsNullOrEmpty(contains))
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    if (content.Contains(contains, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }
                else
                {
                    results.Add(file);
                }
            }

            return new ToolResult(request.Name, true, results.Count > 0 ? string.Join("\n", results) : "No files matched.", null);
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

    private async Task<ToolResult> StoreMemoryAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        var fact = GetStringArg(request.Arguments, "fact");
        if (string.IsNullOrEmpty(fact)) return new ToolResult(request.Name, false, "", "Fact is required");

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
        if (string.IsNullOrEmpty(query)) return new ToolResult(request.Name, false, "", "Query is required");

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

        var pending = MessageQueue.GetPending(sessionId);
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
            var pendingSteer = MessageQueue.GetPendingSteer(sessionId);
            msg = pendingSteer.FirstOrDefault() ?? MessageQueue.GetPending(sessionId).FirstOrDefault();
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

        if (string.IsNullOrEmpty(name)) return new ToolResult(request.Name, false, "", "Name is required");
        if (string.IsNullOrEmpty(desc)) return new ToolResult(request.Name, false, "", "Description is required");
        if (string.IsNullOrEmpty(lang)) return new ToolResult(request.Name, false, "", "Language is required");
        if (string.IsNullOrEmpty(schema)) return new ToolResult(request.Name, false, "", "Parameters schema is required");
        if (string.IsNullOrEmpty(script)) return new ToolResult(request.Name, false, "", "Script content is required");

        if (lang != "powershell" && lang != "python" && lang != "csharp")
        {
            return new ToolResult(request.Name, false, "", "Language must be 'powershell', 'python', or 'csharp'.");
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
                    await validateProc.WaitForExitAsync(ct);
                    if (validateProc.ExitCode != 0)
                    {
                        var syntaxErr = await validateProc.StandardError.ReadToEndAsync(ct);
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

        return new ToolResult(request.Name, true, $"Custom tool '{name}' created successfully. It is now available for use.", null);
    }

    private async Task<ToolResult> DeleteCustomToolAsync(ToolCallRequest request, CancellationToken ct)
    {
        var name = GetStringArg(request.Arguments, "name");
        if (string.IsNullOrEmpty(name)) return new ToolResult(request.Name, false, "", "Name is required");

        await messageStore.DeleteCustomToolAsync(name);
        return new ToolResult(request.Name, true, $"Custom tool '{name}' deleted.", null);
    }

    private async Task<ToolResult> ExecuteCustomToolAsync(ToolCallRequest request, CancellationToken ct)
    {
        var customTools = await messageStore.GetCustomToolsAsync();
        var tool = customTools.FirstOrDefault(t => t.Name == request.Name);
        
        if (tool == null)
            return new ToolResult(request.Name, false, string.Empty, $"Tool '{request.Name}' not implemented.");

        return await Task.Run(async () =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string tempDir = string.Empty;
                string tempFile = string.Empty;

                if (tool.Language == "python")
                {
                    tempFile = Path.GetTempFileName() + ".py";
                    File.WriteAllText(tempFile, tool.ScriptContent);
                    psi.FileName = "python";
                    psi.Arguments = $"\"{tempFile}\"";
                }
                else if (tool.Language == "csharp")
                {
                    tempDir = Path.Combine(Path.GetTempPath(), "KlydisCustomTool_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    
                    var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>";
                    File.WriteAllText(Path.Combine(tempDir, "Tool.csproj"), csproj);
                    
                    var code = tool.ScriptContent;
                    // If no namespace/class is defined, wrap it in a top-level statement or just use it if they wrote one.
                    // We'll assume the model writes a valid Program.cs
                    File.WriteAllText(Path.Combine(tempDir, "Program.cs"), code);

                    psi.FileName = "dotnet";
                    psi.Arguments = $"run --project \"{tempDir}\"";
                }
                else // powershell
                {
                    tempFile = Path.GetTempFileName() + ".ps1";
                    File.WriteAllText(tempFile, tool.ScriptContent);
                    psi.FileName = "powershell.exe";
                    psi.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempFile}\"";
                }

                foreach (var arg in request.Arguments)
                {
                    if (arg.Value != null)
                    {
                        var stringVal = arg.Value.ToString() ?? "";
                        if (arg.Value is JsonElement jsonElement)
                        {
                            if (jsonElement.ValueKind == JsonValueKind.String) stringVal = jsonElement.GetString() ?? "";
                            else stringVal = jsonElement.GetRawText();
                        }
                        psi.EnvironmentVariables[arg.Key] = stringVal;
                    }
                }

                using var process = Process.Start(psi);
                if (process == null) return new ToolResult(request.Name, false, "", "Failed to start process");

                // Windows Python asyncio subprocess bug wrapper not needed here as this is C# spawning the process, not Python.

                if (!process.WaitForExit(120000)) // 2 min timeout to allow for dotnet run compilation
                {
                    process.Kill();
                    return new ToolResult(request.Name, false, "", "Custom tool timed out after 120 seconds");
                }

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                
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
            return new ToolResult(request.Name, false, string.Empty, "Both 'name' and 'prompt_instruction' are required to learn a skill.");
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
}
