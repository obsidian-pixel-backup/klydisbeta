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
    Klydis.Core.Skills.SkillLibraryManager? skillLibraryManager = null)
{
    private static readonly HttpClient _httpClient = new HttpClient();
    
    public ModelMessageQueue? MessageQueue { get; set; } = messageQueue;
    public Klydis.Core.Skills.SkillLibraryManager? SkillLibraryManager { get; set; } = skillLibraryManager;

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
        new ToolDefinition("read_file", "Reads content from a file", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the file", true)
        }, false),
        new ToolDefinition("write_file", "Writes content to a file", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the file", true),
            new("content", "string", "Content to write", true)
        }, false),
        new ToolDefinition("list_directory", "Lists directory contents with sizes", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the directory", true)
        }, false),
        new ToolDefinition("run_command", "Executes PowerShell command", new List<ToolParameter>
        {
            new("command", "string", "Command to execute", true)
        }, false),
        new ToolDefinition("get_system_info", "Returns CPU, RAM, GPU, and disk info", new List<ToolParameter>(), false),
        new ToolDefinition("search_web", "Searches the web for a query", new List<ToolParameter>
        {
            new("query", "string", "Search query", true)
        }, false),
        new ToolDefinition("crawl_url", "Fetches JS-rendered HTML from URL using a headless browser and converts it to clean Markdown for LLMs.", new List<ToolParameter>
        {
            new("url", "string", "Target URL", true)
        }, false),
        new ToolDefinition("search_files", "Searches directory for files matching pattern or containing text", new List<ToolParameter>
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
        new ToolDefinition("check_message_queue", "Checks pending user messages waiting in the processing queue for the active session.", new List<ToolParameter>(), false),
        new ToolDefinition("incorporate_queued_message", "Retrieves and incorporates a pending queued message by queue_id to steer the current reasoning or execution task.", new List<ToolParameter>
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
            return new ToolResult(request.Name, false, string.Empty, $"Tool '{request.Name}' not found.");
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

            var offloadedMessage = $"[Tool Output Exceeded Context Budget]\n" +
                                   $"Full output ({result.Output.Length} characters) offloaded to: {filePath}\n\n" +
                                   $"Preview (First {preview.Length} characters):\n" +
                                   $"--------------------------------------------------\n" +
                                   $"{preview}\n" +
                                   $"--------------------------------------------------\n" +
                                   $"[To view full or detailed content, use tool read_file with path: {filePath}]";

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

        // Handle standard command chaining operators (&& -> ;) for PowerShell compatibility
        var sanitizedCmd = Regex.Replace(command, @"(?<=\s|^)&&(?=\s|$)", ";");
        var encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(sanitizedCmd));

        // Use Task.Run to wrap synchronous process execution
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedCmd}",
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return new ToolResult(request.Name, false, "", "Failed to start process");

                // Timeout of 60 seconds
                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(); } catch { }
                    return new ToolResult(request.Name, false, "", "Command timed out after 60 seconds");
                }

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                
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
        }, ct);
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

    private async Task<ToolResult> SearchWebAsync(ToolCallRequest request, CancellationToken ct)
    {
        var query = GetStringArg(request.Arguments, "query");
        if (string.IsNullOrEmpty(query)) return new ToolResult(request.Name, false, "", "Query is required");

        try
        {
            var results = new List<string>();

            // Attempt Bing Search (using a clean request configuration without user agent headers to bypass bot blocks)
            try
            {
                var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";
                var requestMsg = new HttpRequestMessage(HttpMethod.Get, url);
                
                var response = await _httpClient.SendAsync(requestMsg, ct);
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(ct);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    
                    var algoNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'b_algo')]");
                    if (algoNodes != null)
                    {
                        foreach (var node in algoNodes.Take(5))
                        {
                            var titleNode = node.SelectSingleNode(".//h2/a") ?? node.SelectSingleNode(".//a");
                            var title = titleNode != null ? HtmlEntity.DeEntitize(titleNode.InnerText).Trim() : "No Title";
                            var link = titleNode != null ? titleNode.GetAttributeValue("href", "") : "";
                            
                            var snippetNode = node.SelectSingleNode(".//p") ?? node.SelectSingleNode(".//div[contains(@class, 'b_caption')]/p") ?? node.SelectSingleNode(".//span");
                            var snippet = snippetNode != null ? HtmlEntity.DeEntitize(snippetNode.InnerText).Trim() : "No Snippet";
                            
                            snippet = Regex.Replace(snippet, @"\s+", " ");
                            results.Add($"Title: {title}\nLink: {link}\nSnippet: {snippet}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bing search failed; will attempt Wikipedia fallback.");
            }

            // Fallback to Wikipedia OpenSearch if no results were fetched
            if (results.Count == 0)
            {
                try
                {
                    var wikiUrl = $"https://en.wikipedia.org/w/api.php?action=opensearch&search={Uri.EscapeDataString(query)}&limit=5&namespace=0&format=json";
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
                            int count = Math.Min(titles.GetArrayLength(), 5);
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
                return new ToolResult(request.Name, true, "No results found. (The search engine might be blocking the request)", null);
            }
            return new ToolResult(request.Name, true, string.Join("\n\n", results), null);
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
            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            
            // Go to page and wait for network to be idle
            await page.GotoAsync(url, new Microsoft.Playwright.PageGotoOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle, Timeout = 30000 });
            
            // Get HTML of the body
            var html = await page.InnerHTMLAsync("body");
            
            // Convert to Markdown
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
            
            if (markdown.Length > 20000) markdown = markdown[..20000] + "... [TRUNCATED]";

            return new ToolResult(request.Name, true, markdown, null);
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

        var newWorldState = string.IsNullOrEmpty(session.WorldState) ? fact : $"{session.WorldState}\n- {fact}";
        await messageStore.UpdateSessionAsync(sessionId, null, newWorldState, null);
        
        return new ToolResult(request.Name, true, "Fact stored successfully in session World State.", null);
    }

    private async Task<ToolResult> RetrieveMemoryAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        var query = GetStringArg(request.Arguments, "query");
        if (string.IsNullOrEmpty(query)) return new ToolResult(request.Name, false, "", "Query is required");

        var results = await messageStore.SearchMessagesAsync(sessionId, query, 5);
        if (results.Count == 0) return new ToolResult(request.Name, true, "No relevant past messages found.", null);

        var output = string.Join("\n\n", results.Select(r => $"[{r.Message.Timestamp}] {r.Message.Role}: {r.Message.Content}"));
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
            msg = MessageQueue.GetById(queueId);
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
}
