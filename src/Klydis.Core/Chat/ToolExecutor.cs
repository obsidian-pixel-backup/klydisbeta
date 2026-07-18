using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
public class ToolExecutor(ILogger<ToolExecutor> logger)
{
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
    public async Task<ToolResult> ExecuteToolAsync(ToolCallRequest request, CancellationToken ct)
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
                "search_web" => new ToolResult(request.Name, true, "Web search not yet implemented", null),
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
}
