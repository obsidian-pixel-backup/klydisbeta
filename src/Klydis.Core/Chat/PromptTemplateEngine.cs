using System;
using System.Collections.Generic;
using System.Text;

namespace Klydis.Core.Chat;

/// <summary>
/// Represents the role of a message in a chat conversation.
/// </summary>
public enum ChatRole
{
    /// <summary>System instruction</summary>
    System,
    /// <summary>User input</summary>
    User,
    /// <summary>Assistant response</summary>
    Assistant,
    /// <summary>Tool observation/result</summary>
    Tool
}

/// <summary>
/// Represents a chat message.
/// </summary>
public record ChatMessage(ChatRole Role, string Content, string? Name = null);

/// <summary>
/// Supported chat templates.
/// </summary>
public enum ChatTemplate
{
    ChatML,
    Llama3,
    Mistral,
    Gemma,
    Phi,
    Qwen,
    Generic
}

/// <summary>
/// Handles chat template formatting for different model architectures.
/// </summary>
public class PromptTemplateEngine
{
    /// <summary>
    /// Formats a list of messages into a single prompt string based on the specified template.
    /// </summary>
    public string ApplyTemplate(IList<ChatMessage> messages, ChatTemplate template)
    {
        var sb = new StringBuilder();

        switch (template)
        {
            case ChatTemplate.ChatML:
            case ChatTemplate.Qwen:
                foreach (var msg in messages)
                {
                    string roleStr = msg.Role switch
                    {
                        ChatRole.System => "system",
                        ChatRole.User => "user",
                        ChatRole.Assistant => "assistant",
                        ChatRole.Tool => "user",
                        _ => "user"
                    };
                    string content = msg.Role == ChatRole.Tool 
                        ? (string.IsNullOrEmpty(msg.Name) ? $"<tool_response>\n{msg.Content}\n</tool_response>" : $"<tool_response>\n[Tool Result for '{msg.Name}']:\n{msg.Content}\n</tool_response>")
                        : msg.Content;
                    sb.Append($"<|im_start|>{roleStr}\n{content}<|im_end|>\n");
                }
                sb.Append("<|im_start|>assistant\n");
                break;

            case ChatTemplate.Llama3:
                sb.Append("<|begin_of_text|>");
                foreach (var msg in messages)
                {
                    string roleStr = msg.Role switch
                    {
                        ChatRole.System => "system",
                        ChatRole.User => "user",
                        ChatRole.Assistant => "assistant",
                        ChatRole.Tool => "ipython",
                        _ => "user"
                    };
                    string content = msg.Role == ChatRole.Tool && !string.IsNullOrEmpty(msg.Name) 
                        ? $"[Tool Result for '{msg.Name}']:\n{msg.Content}" 
                        : msg.Content;
                    sb.Append($"<|start_header_id|>{roleStr}<|end_header_id|>\n\n{content}<|eot_id|>\n");
                }
                sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
                break;

            case ChatTemplate.Mistral:
                bool hasSystem = false;
                string systemPrompt = "";
                foreach (var msg in messages)
                {
                    if (msg.Role == ChatRole.System)
                    {
                        systemPrompt += msg.Content + "\n\n";
                        hasSystem = true;
                    }
                    else if (msg.Role == ChatRole.User)
                    {
                        if (hasSystem)
                        {
                            sb.Append($"[INST] {systemPrompt}{msg.Content} [/INST] ");
                            hasSystem = false;
                            systemPrompt = "";
                        }
                        else
                        {
                            sb.Append($"[INST] {msg.Content} [/INST] ");
                        }
                    }
                    else if (msg.Role == ChatRole.Assistant)
                    {
                        sb.Append($"{msg.Content}</s> ");
                    }
                    else if (msg.Role == ChatRole.Tool)
                    {
                        string toolName = !string.IsNullOrEmpty(msg.Name) ? $" '{msg.Name}'" : "";
                        sb.Append($"[INST] Tool Result{toolName}: {msg.Content} [/INST] ");
                    }
                }
                break;

            case ChatTemplate.Gemma:
                foreach (var msg in messages)
                {
                    string roleStr = msg.Role switch
                    {
                        ChatRole.System => "user", // Gemma doesn't have explicit system, wrap in user
                        ChatRole.User => "user",
                        ChatRole.Assistant => "model",
                        ChatRole.Tool => "user",
                        _ => "user"
                    };
                    string content = msg.Role == ChatRole.Tool 
                        ? (!string.IsNullOrEmpty(msg.Name) ? $"[Tool Result for '{msg.Name}']:\n{msg.Content}" : $"[Tool Result]:\n{msg.Content}")
                        : msg.Content;
                    sb.Append($"<start_of_turn>{roleStr}\n{content}<end_of_turn>\n");
                }
                sb.Append("<start_of_turn>model\n");
                break;

            case ChatTemplate.Phi:
                foreach (var msg in messages)
                {
                    string roleStr = msg.Role switch
                    {
                        ChatRole.System => "system",
                        ChatRole.User => "user",
                        ChatRole.Assistant => "assistant",
                        ChatRole.Tool => "user",
                        _ => "user"
                    };
                    string content = msg.Role == ChatRole.Tool 
                        ? (!string.IsNullOrEmpty(msg.Name) ? $"[Tool Result for '{msg.Name}']:\n{msg.Content}" : $"[Tool Result]:\n{msg.Content}")
                        : msg.Content;
                    sb.Append($"<|{roleStr}|>\n{content}<|end|>\n");
                }
                sb.Append("<|assistant|>\n");
                break;

            case ChatTemplate.Generic:
            default:
                foreach (var msg in messages)
                {
                    string prefix = msg.Role switch
                    {
                        ChatRole.Tool => !string.IsNullOrEmpty(msg.Name) ? $"Tool ({msg.Name})" : "Tool",
                        _ => msg.Role.ToString()
                    };
                    sb.Append($"{prefix}: {msg.Content}\n");
                }
                sb.Append("Assistant: ");
                break;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Auto-detects the chat template from a GGUF architecture string.
    /// </summary>
    public ChatTemplate DetectTemplate(string architecture)
    {
        var arch = architecture.ToLowerInvariant();
        if (arch.Contains("llama")) return ChatTemplate.Llama3;
        if (arch.Contains("qwen")) return ChatTemplate.Qwen;
        if (arch.Contains("mistral") || arch.Contains("mixtral")) return ChatTemplate.Mistral;
        if (arch.Contains("gemma")) return ChatTemplate.Gemma;
        if (arch.Contains("phi")) return ChatTemplate.Phi;
        if (arch.Contains("chatml")) return ChatTemplate.ChatML;
        
        return ChatTemplate.ChatML; // Default fallback
    }

    /// <summary>
    /// Returns the stop tokens for the given template.
    /// </summary>
    public string[] GetStopTokens(ChatTemplate template)
    {
        return template switch
        {
            ChatTemplate.ChatML or ChatTemplate.Qwen => new[] { "<|im_end|>", "<|im_start|>" },
            ChatTemplate.Llama3 => new[] { "<|eot_id|>", "<|end_of_text|>" },
            ChatTemplate.Mistral => new[] { "</s>" },
            ChatTemplate.Gemma => new[] { "<end_of_turn>" },
            ChatTemplate.Phi => new[] { "<|end|>" },
            _ => Array.Empty<string>()
        };
    }
}
