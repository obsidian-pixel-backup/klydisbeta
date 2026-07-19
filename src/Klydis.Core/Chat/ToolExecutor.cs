using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
public class ToolExecutor(ILogger<ToolExecutor> logger, Klydis.Core.Memory.MessageStore messageStore, Klydis.Core.Memory.ContextOrchestrator contextOrchestrator)
{
    private static readonly HttpClient _httpClient = new HttpClient();
    
    /// <summary>
    /// Gets or sets the current risk level mode.
    /// </summary>
    public RiskLevel CurrentRiskLevel { get; set; } = RiskLevel.Standard;

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
        }, false)
    };

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
            var args = new ToolApprovalEventArgs(request);
            ToolApprovalRequested?.Invoke(this, args);
            if (!args.IsApproved)
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
                "create_custom_tool" => await CreateCustomToolAsync(request, ct),
                "delete_custom_tool" => await DeleteCustomToolAsync(request, ct),
                _ => await ExecuteCustomToolAsync(request, ct)
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing tool {ToolName}", request.Name);
            result = new ToolResult(request.Name, false, string.Empty, ex.Message);
        }

        ToolExecuted?.Invoke(this, result);
        return result;
    }

    private bool IsRiskyRequest(ToolCallRequest request)
    {
        var contentToCheck = "";
        if (request.Arguments != null)
        {
            foreach (var arg in request.Arguments)
            {
                contentToCheck += arg.Value?.ToString() + " ";
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
        var path = request.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
        if (string.IsNullOrEmpty(path)) return new ToolResult(request.Name, false, "", "Path is required");
        if (!File.Exists(path)) return new ToolResult(request.Name, false, "", "File not found");

        var content = await File.ReadAllTextAsync(path, ct);
        if (content.Length > 10000) content = content[..10000] + "... [TRUNCATED]";
        
        return new ToolResult(request.Name, true, content, null);
    }

    private async Task<ToolResult> WriteFileAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = request.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
        var content = request.Arguments.TryGetValue("content", out var c) ? c?.ToString() : null;
        if (string.IsNullOrEmpty(path)) return new ToolResult(request.Name, false, "", "Path is required");
        if (content == null) return new ToolResult(request.Name, false, "", "Content is required");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        
        await File.WriteAllTextAsync(path, content, ct);
        return new ToolResult(request.Name, true, "File written successfully", null);
    }

    private Task<ToolResult> ListDirectoryAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = request.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
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
        var command = request.Arguments.TryGetValue("command", out var c) ? c?.ToString() : null;
        if (string.IsNullOrEmpty(command)) return new ToolResult(request.Name, false, "", "Command is required");

        // Use Task.Run to wrap synchronous process execution
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return new ToolResult(request.Name, false, "", "Failed to start process");

                // Timeout of 60 seconds
                if (!process.WaitForExit(60000))
                {
                    process.Kill();
                    return new ToolResult(request.Name, false, "", "Command timed out after 60 seconds");
                }

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                
                var output = stdout;
                if (!string.IsNullOrEmpty(stderr)) output += $"\nSTDERR:\n{stderr}";

                return new ToolResult(request.Name, process.ExitCode == 0, output, process.ExitCode != 0 ? "Command returned non-zero exit code" : null);
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
        var info = $"OS: {Environment.OSVersion}\n" +
                   $"Processors: {Environment.ProcessorCount}\n" +
                   $"Working Set: {Environment.WorkingSet / (1024 * 1024)} MB\n";

        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
            var diskInfo = string.Join(", ", drives.Select(d => $"{d.Name} ({d.TotalFreeSpace / (1024 * 1024 * 1024)}GB free of {d.TotalSize / (1024 * 1024 * 1024)}GB)"));
            info += $"Disks: {diskInfo}\n";
            
            var gpus = new List<string>();
            using var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                gpus.Add(obj["Name"]?.ToString() ?? "Unknown GPU");
            }
            if (gpus.Any()) info += $"GPU(s): {string.Join(", ", gpus)}";
        }
        catch (Exception ex)
        {
            info += $"[Hardware query failed: {ex.Message}]";
        }

        return Task.FromResult(new ToolResult("get_system_info", true, info, null));
    }
#pragma warning restore CA1416

    private async Task<ToolResult> SearchWebAsync(ToolCallRequest request, CancellationToken ct)
    {
        var query = request.Arguments.TryGetValue("query", out var q) ? q?.ToString() : null;
        if (string.IsNullOrEmpty(query)) return new ToolResult(request.Name, false, "", "Query is required");

        try
        {
            var url = "https://lite.duckduckgo.com/lite/";
            var requestMsg = new HttpRequestMessage(HttpMethod.Post, url);
            requestMsg.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64 AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36)");
            requestMsg.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", query) });
            
            var response = await _httpClient.SendAsync(requestMsg, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var results = new List<string>();
            
            var snippetNodes = doc.DocumentNode.SelectNodes("//td[@class='result-snippet']");
            if (snippetNodes != null)
            {
                foreach (var snippet in snippetNodes.Take(5))
                {
                    results.Add(HtmlEntity.DeEntitize(snippet.InnerText).Trim());
                }
            }
            
            if (results.Count == 0) return new ToolResult(request.Name, true, "No results found. (The search engine might be blocking the request)", null);
            return new ToolResult(request.Name, true, string.Join("\n\n", results), null);
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

    private async Task<ToolResult> CrawlUrlAsync(ToolCallRequest request, CancellationToken ct)
    {
        var url = request.Arguments.TryGetValue("url", out var u) ? u?.ToString() : null;
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
            var config = new ReverseMarkdown.Config
            {
                GithubFlavored = true,
                RemoveComments = true,
                SmartHrefHandling = true
            };
            var converter = new ReverseMarkdown.Converter(config);
            var markdown = converter.Convert(html);
            
            if (markdown.Length > 20000) markdown = markdown[..20000] + "... [TRUNCATED]";

            return new ToolResult(request.Name, true, markdown, null);
        }
        catch (Microsoft.Playwright.PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            return new ToolResult(request.Name, false, "", "Browser binaries not found. You must run the playwright installation script first: `playwright.ps1 install`");
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

    private async Task<ToolResult> SearchFilesAsync(ToolCallRequest request, CancellationToken ct)
    {
        var path = request.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
        var pattern = request.Arguments.TryGetValue("pattern", out var pt) ? pt?.ToString() ?? "*.*" : "*.*";
        var contains = request.Arguments.TryGetValue("contains", out var c) ? c?.ToString() : null;

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
        var fact = request.Arguments.TryGetValue("fact", out var f) ? f?.ToString() : null;
        if (string.IsNullOrEmpty(fact)) return new ToolResult(request.Name, false, "", "Fact is required");

        var session = await messageStore.GetSessionAsync(sessionId);
        if (session == null) return new ToolResult(request.Name, false, "", "Session not found");

        var newWorldState = string.IsNullOrEmpty(session.WorldState) ? fact : $"{session.WorldState}\n- {fact}";
        await messageStore.UpdateSessionAsync(sessionId, null, newWorldState, null);
        
        return new ToolResult(request.Name, true, "Fact stored successfully in session World State.", null);
    }

    private async Task<ToolResult> RetrieveMemoryAsync(ToolCallRequest request, string sessionId, CancellationToken ct)
    {
        var query = request.Arguments.TryGetValue("query", out var q) ? q?.ToString() : null;
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

    private async Task<ToolResult> CreateCustomToolAsync(ToolCallRequest request, CancellationToken ct)
    {
        var name = request.Arguments.TryGetValue("name", out var n) ? n?.ToString() : null;
        var desc = request.Arguments.TryGetValue("description", out var d) ? d?.ToString() : null;
        var lang = request.Arguments.TryGetValue("language", out var l) ? l?.ToString()?.ToLowerInvariant() : "powershell";
        var schema = request.Arguments.TryGetValue("parameters_schema", out var s) ? s?.ToString() : null;
        var script = request.Arguments.TryGetValue("script_content", out var sc) ? sc?.ToString() : null;

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
        var name = request.Arguments.TryGetValue("name", out var n) ? n?.ToString() : null;
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
}
