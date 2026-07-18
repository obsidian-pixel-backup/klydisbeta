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

namespace Klydis.Core.Chat;

/// <summary>
/// Represents a parameter for a tool.
/// </summary>
public record ToolParameter(string Name, string Type, string Description, bool Required, string[]? Enum = null);

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
        }, true),
        new ToolDefinition("list_directory", "Lists directory contents with sizes", new List<ToolParameter>
        {
            new("path", "string", "Absolute path to the directory", true)
        }, false),
        new ToolDefinition("run_command", "Executes PowerShell command", new List<ToolParameter>
        {
            new("command", "string", "Command to execute", true)
        }, true),
        new ToolDefinition("get_system_info", "Returns CPU, RAM, GPU, and disk info", new List<ToolParameter>(), false),
        new ToolDefinition("search_web", "Searches the web for a query", new List<ToolParameter>
        {
            new("query", "string", "Search query", true)
        }, false),
        new ToolDefinition("crawl_url", "Fetches HTML from URL and strips tags to return text", new List<ToolParameter>
        {
            new("url", "string", "Target URL", true)
        }, false),
        new ToolDefinition("deep_research", "Breaks down a complex query into sub-queries and researches them", new List<ToolParameter>
        {
            new("topic", "string", "Complex topic to research", true)
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
        new ToolDefinition("summarize_context", "Compresses older messages into the world state to free up context window", new List<ToolParameter>(), false)
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
    /// Gets all tool definitions.
    /// </summary>
    public IList<ToolDefinition> GetToolDefinitions() => _tools;

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
        var toolDef = _tools.FirstOrDefault(t => t.Name == request.Name);
        
        if (toolDef == null)
        {
            return new ToolResult(request.Name, false, string.Empty, $"Tool '{request.Name}' not found.");
        }

        if (toolDef.RequiresApproval)
        {
            var args = new ToolApprovalEventArgs(request);
            ToolApprovalRequested?.Invoke(this, args);
            if (!args.IsApproved)
            {
                return new ToolResult(request.Name, false, string.Empty, "Tool execution denied by user.");
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
                "deep_research" => await DeepResearchAsync(request, ct),
                "search_files" => await SearchFilesAsync(request, ct),
                "store_memory" => await StoreMemoryAsync(request, sessionId, ct),
                "retrieve_memory" => await RetrieveMemoryAsync(request, sessionId, ct),
                "summarize_context" => await SummarizeContextAsync(request, sessionId, ct),
                _ => new ToolResult(request.Name, false, string.Empty, $"Tool '{request.Name}' not implemented.")
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

    private Task<ToolResult> GetSystemInfoAsync(CancellationToken ct)
    {
        var info = $"OS: {Environment.OSVersion}\n" +
                   $"Processors: {Environment.ProcessorCount}\n" +
                   $"Working Set: {Environment.WorkingSet / (1024 * 1024)} MB";
        return Task.FromResult(new ToolResult("get_system_info", true, info, null));
    }

    private async Task<ToolResult> SearchWebAsync(ToolCallRequest request, CancellationToken ct)
    {
        var query = request.Arguments.TryGetValue("query", out var q) ? q?.ToString() : null;
        if (string.IsNullOrEmpty(query)) return new ToolResult(request.Name, false, "", "Query is required");

        try
        {
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            var requestMsg = new HttpRequestMessage(HttpMethod.Get, url);
            requestMsg.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var response = await _httpClient.SendAsync(requestMsg, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var results = new List<string>();
            var nodes = doc.DocumentNode.SelectNodes("//a[@class='result__snippet']");
            var titleNodes = doc.DocumentNode.SelectNodes("//a[@class='result__url']");
            if (nodes != null && titleNodes != null)
            {
                for (int i = 0; i < Math.Min(nodes.Count, titleNodes.Count); i++)
                {
                    results.Add($"URL: {titleNodes[i].GetAttributeValue("href", "").Trim()}\nSnippet: {nodes[i].InnerText.Trim()}");
                }
            }
            if (results.Count == 0) return new ToolResult(request.Name, true, "No results found.", null);
            return new ToolResult(request.Name, true, string.Join("\n\n", results.Take(5)), null);
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
            var requestMsg = new HttpRequestMessage(HttpMethod.Get, url);
            requestMsg.Headers.Add("User-Agent", "Mozilla/5.0");
            var response = await _httpClient.SendAsync(requestMsg, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//nav|//header|//footer|//noscript");
            if (nodesToRemove != null)
            {
                foreach (var node in nodesToRemove) node.Remove();
            }

            var text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 8000) text = text[..8000] + "... [TRUNCATED]";

            return new ToolResult(request.Name, true, text, null);
        }
        catch (Exception ex)
        {
            return new ToolResult(request.Name, false, "", ex.Message);
        }
    }

    private async Task<ToolResult> DeepResearchAsync(ToolCallRequest request, CancellationToken ct)
    {
        var topic = request.Arguments.TryGetValue("topic", out var t) ? t?.ToString() : null;
        if (string.IsNullOrEmpty(topic)) return new ToolResult(request.Name, false, "", "Topic is required");

        // Simulate a multi-step research by doing a few searches around the topic
        var queries = new[] { topic, $"{topic} overview", $"{topic} details" };
        var results = new List<string>();
        foreach (var query in queries)
        {
            var req = new ToolCallRequest("search_web", new Dictionary<string, object> { { "query", query } });
            var res = await SearchWebAsync(req, ct);
            if (res.Success) results.Add($"Query: {query}\n{res.Output}");
        }
        
        var combined = string.Join("\n\n---\n\n", results);
        if (combined.Length > 10000) combined = combined[..10000] + "... [TRUNCATED]";
        return new ToolResult(request.Name, true, combined, null);
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
}
