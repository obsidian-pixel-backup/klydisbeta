using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Chat;

/// <summary>
/// Manages loading, caching, and combining the Klydis Master System Prompt with runtime tool execution directives.
/// </summary>
public class SystemPromptManager
{
    private static readonly object _lock = new();
    private static string? _cachedMasterPrompt;

    /// <summary>
    /// Master System Prompt file name.
    /// </summary>
    public const string MasterPromptFileName = "klydis master system prompt.md";

    private readonly ILogger<SystemPromptManager>? _logger;

    public SystemPromptManager(ILogger<SystemPromptManager>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Loads the Master System Prompt from file or fallback.
    /// Searches the current working directory, application base directory, or .klydis AppData folder.
    /// </summary>
    public static string GetMasterSystemPrompt(string? customPath = null)
    {
        if (_cachedMasterPrompt != null && string.IsNullOrEmpty(customPath))
        {
            return _cachedMasterPrompt;
        }

        lock (_lock)
        {
            if (_cachedMasterPrompt != null && string.IsNullOrEmpty(customPath))
            {
                return _cachedMasterPrompt;
            }

            string?[] candidatePaths = new[]
            {
                customPath,
                Path.Combine(Directory.GetCurrentDirectory(), MasterPromptFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MasterPromptFileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".klydis", MasterPromptFileName)
            };

            foreach (var candidate in candidatePaths)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    try
                    {
                        string content = File.ReadAllText(candidate, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            if (string.IsNullOrEmpty(customPath))
                            {
                                _cachedMasterPrompt = content;
                            }
                            return content;
                        }
                    }
                    catch
                    {
                        // Fallback to next candidate
                    }
                }
            }

            // Fallback default master system prompt profile
            string fallback = GetDefaultFallbackMasterPrompt();
            if (string.IsNullOrEmpty(customPath))
            {
                _cachedMasterPrompt = fallback;
            }
            return fallback;
        }
    }

    /// <summary>
    /// Clears the cached master prompt to force reload from disk.
    /// </summary>
    public static void ResetCache()
    {
        lock (_lock)
        {
            _cachedMasterPrompt = null;
        }
    }

    /// <summary>
    /// Candidate file names for User Style Modes.
    /// </summary>
    public static readonly string[] UserStyleFileNameCandidates = new[]
    {
        "UserStyle_Modes.md",
        "user styles mode.md",
        "UserStyle_Mode.md",
        "UserStyles.md"
    };

    /// <summary>
    /// Locates the User Style Modes file across workspace, application, parent, and user directories.
    /// </summary>
    public static string? GetUserStylesFilePath()
    {
        var candidateDirectories = new List<string>
        {
            Directory.GetCurrentDirectory(),
            AppDomain.CurrentDomain.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".klydis")
        };

        foreach (var startDir in new[] { Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(startDir)) continue;
            var dirInfo = new DirectoryInfo(startDir);
            while (dirInfo != null && dirInfo.Exists)
            {
                if (!candidateDirectories.Contains(dirInfo.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    candidateDirectories.Add(dirInfo.FullName);
                }
                dirInfo = dirInfo.Parent;
            }
        }

        foreach (var dir in candidateDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (var fileName in UserStyleFileNameCandidates)
            {
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Parses personality modes from UserStyle_Modes.md file into a dictionary.
    /// </summary>
    public static Dictionary<string, string> ParseUserStyleModes()
    {
        var modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        modes["Default"] = string.Empty;

        string? filePath = GetUserStylesFilePath();
        if (filePath == null || !File.Exists(filePath))
        {
            return modes;
        }

        try
        {
            string text = File.ReadAllText(filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return modes;

            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string? currentModeName = null;
            var currentModeContent = new StringBuilder();

            foreach (var line in lines)
            {
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    if (currentModeName != null)
                    {
                        modes[currentModeName] = currentModeContent.ToString().Trim();
                        currentModeContent.Clear();
                    }

                    currentModeName = line.Substring(2).Trim();
                }
                else if (currentModeName != null)
                {
                    currentModeContent.AppendLine(line);
                }
            }

            if (currentModeName != null)
            {
                modes[currentModeName] = currentModeContent.ToString().Trim();
            }
        }
        catch
        {
            // Fallback silently if reading fails
        }

        return modes;
    }

    /// <summary>
    /// Gets all available personality mode titles.
    /// </summary>
    public static List<string> GetAvailablePersonalities()
    {
        var modes = ParseUserStyleModes();
        return modes.Keys.ToList();
    }

    /// <summary>
    /// Gets the prompt text for a specific personality mode.
    /// </summary>
    public static string? GetPersonalityPrompt(string? personalityName)
    {
        if (string.IsNullOrWhiteSpace(personalityName) || personalityName.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var modes = ParseUserStyleModes();
        if (modes.TryGetValue(personalityName, out var content) && !string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var trimmedName = personalityName.Replace(" Mode", "", StringComparison.OrdinalIgnoreCase).Trim();
        foreach (var kvp in modes)
        {
            if (kvp.Key.Replace(" Mode", "", StringComparison.OrdinalIgnoreCase).Trim().Equals(trimmedName, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Combines the Klydis Master System Prompt profile with runtime tool schema, steering directives, active headers, and personality modes.
    /// </summary>
    public string BuildCombinedPrompt(
        string toolsSchema,
        string worldStateHeader = "",
        string queueNotice = "",
        string ragNotice = "",
        string skillHeader = "",
        string? customPath = null,
        string? personalityMode = null)
    {
        string masterPrompt = GetMasterSystemPrompt(customPath);
        string? personalityContent = GetPersonalityPrompt(personalityMode);

        var sb = new StringBuilder();

        // Section 1: Klydis Master System Prompt (Persona, Profile, Safety, Tone, Copyright & Search Rules)
        sb.AppendLine(masterPrompt.Trim());
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(personalityContent))
        {
            sb.AppendLine("## ACTIVE MODEL PERSONALITY & USER STYLE DIRECTIVES");
            sb.AppendLine("You MUST strictly adhere to the following personality style for all your responses:");
            sb.AppendLine(personalityContent.Trim());
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        // Section 2: Active Runtime Engine & Tool Execution Directives
        sb.AppendLine("## RUNTIME EXECUTION DIRECTIVES & TOOL INTEGRATION");
        sb.AppendLine("You are operating as Klydis in the local agentic desktop environment. You have access to the following local tools:");
        sb.AppendLine(toolsSchema);
        sb.AppendLine();
        sb.AppendLine("### TOOL USAGE STRATEGY & BEHAVIORAL RULES");
        sb.AppendLine("- NEVER repeat a tool call with identical arguments. If you already received a result, USE IT.");
        sb.AppendLine("- ALWAYS analyze tool results before making additional calls.");
        sb.AppendLine("- If a tool returns an error, try a DIFFERENT approach (different tool or different arguments).");
        sb.AppendLine("- Do not invent custom tool names (e.g. video-downloader, start-app). Only use tools defined in the tool schema.");
        sb.AppendLine();
        sb.AppendLine("### QUEUED MESSAGES & STEERING STRATEGY");
        sb.AppendLine("- Periodically check for queued messages using 'check_message_queue' during long, multi-step operations or extended reasoning workflows, or whenever you require additional user context.");
        sb.AppendLine("- When pending queued messages are available, call tool 'incorporate_queued_message' with argument {\"queue_id\": \"<ID>\"} to retrieve and steer using the user's queued commands or context.");
        sb.AppendLine();
        sb.AppendLine("### RAG VECTOR SEARCH & WORKSPACE RETRIEVAL STRATEGY");
        sb.AppendLine("- Use 'search_rag' to search indexed project files, workspace code, and document collections when answering questions about indexed projects or codebases.");
        sb.AppendLine("- Use 'list_rag_collections' to view what project workspace folders are currently indexed in your RAG vector store.");
        sb.AppendLine("- Use 'index_folder_rag' when asked to index or ingest a local project directory into your vector store for future RAG queries.");
        sb.AppendLine();
        sb.AppendLine("### POWERSHELL & WINDOWS GUIDANCE");
        sb.AppendLine("- Only use real, built-in PowerShell cmdlets (Get-Process, Start-Process, Get-ChildItem, Get-Service). Do NOT fabricate cmdlet names (e.g. Get-AppProcessList).");
        sb.AppendLine("- For launching apps: Use Start-Process with FilePath and ArgumentList. Example: Start-Process -FilePath \"chrome.exe\" -ArgumentList \"https://youtube.com\"");
        sb.AppendLine("- For large directory listings (e.g. C:\\Windows\\system32): Always pipe through Select-Object -First N to prevent timeouts.");
        sb.AppendLine();
        sb.AppendLine("### WEB BROWSING STRATEGY");
        sb.AppendLine("- Use 'search_web' for general queries, current events, weather, news, factual lookups. This tool utilizes a stealth browser engine to safely fetch real-time search engine results without being blocked.");
        sb.AppendLine("- Use 'crawl_url' when you need the full content of a specific page (documentation, articles, web apps). It renders dynamic JavaScript and bypasses anti-bot verification.");
        sb.AppendLine("- After receiving search/crawl results, SUMMARIZE the key information concisely for the user. Do NOT dump raw search output.");
        sb.AppendLine();
        sb.AppendLine("### IMPORTANT INSTRUCTIONS FOR TOOL CALLING AND THINKING");
        sb.AppendLine("1. If you need to think or plan, use <think>...</think> tags FIRST.");
        sb.AppendLine("2. You MUST NOT output <tool_call> inside <think> tags. Tool calls must be placed AFTER the </think> closing tag.");
        sb.AppendLine("3. To use a tool, output a JSON block exactly like this: <tool_call>{\"name\": \"tool_name\", \"arguments\": {...}}</tool_call>");
        sb.AppendLine("4. CRITICAL: Whenever the user asks you to perform an action, test tools, inspect system/files, execute commands, or manage skills, YOU MUST CALL THE TOOL IMMEDIATELY using the <tool_call> tag. Do not just state that you will run a tool—OUTPUT THE <tool_call> TAG DIRECTLY.");
        sb.AppendLine("5. SKILL BRAIN & LEARNING: You are connected to a Skills Library Brain. You can use 'list_skills' or 'search_skills' to discover skills, 'get_skill_details' or 'activate_skill' to inspect/activate specialized domain instructions, and 'learn_skill' to create and save new custom skills to your library brain when learning new workflows or user directives.");
        sb.AppendLine("6. Examples of tool calls:");
        sb.AppendLine("   - Call tool with no arguments: <tool_call>{\"name\": \"get_system_info\", \"arguments\": {}}</tool_call>");
        sb.AppendLine("   - Launch app: <tool_call>{\"name\": \"run_command\", \"arguments\": {\"command\": \"Start-Process -FilePath \\\"chrome.exe\\\" -ArgumentList \\\"https://youtube.com\\\"\"}}</tool_call>");
        sb.AppendLine("   - Search skills: <tool_call>{\"name\": \"search_skills\", \"arguments\": {\"query\": \"wpf\"}}</tool_call>");
        sb.AppendLine("   - Search RAG: <tool_call>{\"name\": \"search_rag\", \"arguments\": {\"query\": \"InferenceEngine model loading\"}}</tool_call>");
        sb.AppendLine("7. You can provide normal text before or after tool calls outside of think tags.");
        sb.AppendLine("8. Tool results will be provided to you in subsequent messages. Analyze the result before proceeding.");
        sb.AppendLine("9. DO NOT repeat the exact same tool call if it just failed or returned an error.");

        if (!string.IsNullOrWhiteSpace(worldStateHeader))
        {
            sb.Append(worldStateHeader);
        }
        if (!string.IsNullOrWhiteSpace(queueNotice))
        {
            sb.Append(queueNotice);
        }
        if (!string.IsNullOrWhiteSpace(ragNotice))
        {
            sb.Append(ragNotice);
        }
        if (!string.IsNullOrWhiteSpace(skillHeader))
        {
            sb.Append(skillHeader);
        }

        return sb.ToString();
    }

    private static string GetDefaultFallbackMasterPrompt()
    {
        return @"# Klydis System Prompt Profile
You are Klydis, an advanced AI assistant created by the Klydis team. You are direct, helpful, cooperative, and highly capable in software development, reasoning, research, document creation, and local system tasks.
You fulfill user requests directly and thoroughly while maintaining user wellbeing, tone clarity, and safety excellence.";
    }
}
