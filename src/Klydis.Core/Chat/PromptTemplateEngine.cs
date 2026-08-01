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
/// Supported chat templates across model architectures and fine-tunes.
/// </summary>
public enum ChatTemplate
{
    ChatML,
    Llama3,
    Llama2,
    Mistral,
    Gemma,
    Phi,
    Qwen,
    DeepSeek,
    CommandR,
    Alpaca,
    Vicuna,
    Generic
}

/// <summary>
/// Handles chat template formatting for different model architectures and fine-tune types.
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
            case ChatTemplate.DeepSeek:
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

            case ChatTemplate.Llama2:
                string llama2Sys = "";
                foreach (var msg in messages)
                {
                    if (msg.Role == ChatRole.System)
                    {
                        llama2Sys += msg.Content + "\n\n";
                    }
                    else if (msg.Role == ChatRole.User)
                    {
                        if (!string.IsNullOrEmpty(llama2Sys))
                        {
                            sb.Append($"[INST] <<SYS>>\n{llama2Sys.Trim()}\n<</SYS>>\n\n{msg.Content} [/INST] ");
                            llama2Sys = "";
                        }
                        else
                        {
                            sb.Append($"[INST] {msg.Content} [/INST] ");
                        }
                    }
                    else if (msg.Role == ChatRole.Assistant)
                    {
                        sb.Append($"{msg.Content} </s>");
                    }
                    else if (msg.Role == ChatRole.Tool)
                    {
                        sb.Append($"[INST] [Tool Result]: {msg.Content} [/INST] ");
                    }
                }
                break;

            case ChatTemplate.Mistral:
                string mistralSys = "";
                foreach (var msg in messages)
                {
                    if (msg.Role == ChatRole.System)
                    {
                        mistralSys += msg.Content + "\n\n";
                    }
                    else if (msg.Role == ChatRole.User)
                    {
                        if (!string.IsNullOrEmpty(mistralSys))
                        {
                            sb.Append($"[INST] {mistralSys}{msg.Content} [/INST] ");
                            mistralSys = "";
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
                        ChatRole.System => "user",
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

            case ChatTemplate.CommandR:
                foreach (var msg in messages)
                {
                    string roleToken = msg.Role switch
                    {
                        ChatRole.System => "SYSTEM",
                        ChatRole.User => "USER",
                        ChatRole.Assistant => "CHATBOT",
                        ChatRole.Tool => "USER",
                        _ => "USER"
                    };
                    string content = msg.Role == ChatRole.Tool
                        ? $"[Tool Result]: {msg.Content}"
                        : msg.Content;
                    sb.Append($"<|START_OF_TURN_TOKEN|><|{roleToken}_TOKEN|>{content}<|END_OF_TURN_TOKEN|>");
                }
                sb.Append("<|START_OF_TURN_TOKEN|><|CHATBOT_TOKEN|>");
                break;

            case ChatTemplate.Alpaca:
                string alpacaSys = "";
                foreach (var msg in messages)
                {
                    if (msg.Role == ChatRole.System)
                    {
                        alpacaSys += msg.Content + "\n\n";
                    }
                    else if (msg.Role == ChatRole.User)
                    {
                        sb.Append($"### Instruction:\n{alpacaSys}{msg.Content}\n\n### Response:\n");
                        alpacaSys = "";
                    }
                    else if (msg.Role == ChatRole.Assistant)
                    {
                        sb.Append($"{msg.Content}\n\n");
                    }
                    else if (msg.Role == ChatRole.Tool)
                    {
                        sb.Append($"[Tool Result]: {msg.Content}\n\n");
                    }
                }
                break;

            case ChatTemplate.Vicuna:
                string vicunaSys = "";
                foreach (var msg in messages)
                {
                    if (msg.Role == ChatRole.System)
                    {
                        vicunaSys += msg.Content + "\n\n";
                    }
                    else if (msg.Role == ChatRole.User)
                    {
                        sb.Append($"USER: {vicunaSys}{msg.Content}\nASSISTANT: ");
                        vicunaSys = "";
                    }
                    else if (msg.Role == ChatRole.Assistant)
                    {
                        sb.Append($"{msg.Content}</s>");
                    }
                    else if (msg.Role == ChatRole.Tool)
                    {
                        sb.Append($"USER: [Tool Result]: {msg.Content}\nASSISTANT: ");
                    }
                }
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
    /// Auto-detects the chat template from architecture, model file path, embedded GGUF template string, or fine-tune tags.
    /// </summary>
    public ChatTemplate DetectTemplate(
        string architecture, 
        string? modelPath = null, 
        string? rawChatTemplate = null, 
        string? fineTuneName = null, 
        string? templateOverride = null)
    {
        // 1. Explicit template override
        if (!string.IsNullOrWhiteSpace(templateOverride) && Enum.TryParse<ChatTemplate>(templateOverride, true, out var parsedOverride))
        {
            return parsedOverride;
        }

        // 2. Embedded GGUF template string inspection
        if (!string.IsNullOrWhiteSpace(rawChatTemplate))
        {
            if (rawChatTemplate.Contains("<|im_start|>")) return ChatTemplate.ChatML;
            if (rawChatTemplate.Contains("<|start_header_id|>")) return ChatTemplate.Llama3;
            if (rawChatTemplate.Contains("[INST]") && rawChatTemplate.Contains("<<SYS>>")) return ChatTemplate.Llama2;
            if (rawChatTemplate.Contains("[INST]")) return ChatTemplate.Mistral;
            if (rawChatTemplate.Contains("<start_of_turn>")) return ChatTemplate.Gemma;
            if (rawChatTemplate.Contains("<|user|>") || rawChatTemplate.Contains("<|end|>")) return ChatTemplate.Phi;
            if (rawChatTemplate.Contains("<|START_OF_TURN_TOKEN|>")) return ChatTemplate.CommandR;
            if (rawChatTemplate.Contains("### Instruction:")) return ChatTemplate.Alpaca;
            if (rawChatTemplate.Contains("USER:") && rawChatTemplate.Contains("ASSISTANT:")) return ChatTemplate.Vicuna;
        }

        // 3. Inspect model filename and fine-tune tags (fine-tunes often use ChatML regardless of base architecture)
        string combinedName = ((modelPath != null ? Path.GetFileName(modelPath) : "") + " " + (fineTuneName ?? "")).ToLowerInvariant();
        
        if (combinedName.Contains("liquid") || combinedName.Contains("openchat") || combinedName.Contains("heretic") || combinedName.Contains("uncensored"))
        {
            return ChatTemplate.ChatML;
        }
        if (combinedName.Contains("deepseek")) return ChatTemplate.DeepSeek;
        if (combinedName.Contains("command-r") || combinedName.Contains("commandr")) return ChatTemplate.CommandR;
        if (combinedName.Contains("alpaca")) return ChatTemplate.Alpaca;
        if (combinedName.Contains("vicuna")) return ChatTemplate.Vicuna;
        if (combinedName.Contains("llama-2") || combinedName.Contains("llama2") || combinedName.Contains("codellama")) return ChatTemplate.Llama2;
        if (combinedName.Contains("llama-3") || combinedName.Contains("llama3")) return ChatTemplate.Llama3;

        // 4. Fallback to GGUF architecture string
        if (!string.IsNullOrWhiteSpace(architecture))
        {
            var arch = architecture.ToLowerInvariant();
            if (arch.Contains("llama")) return ChatTemplate.Llama3;
            if (arch.Contains("qwen")) return ChatTemplate.Qwen;
            if (arch.Contains("mistral") || arch.Contains("mixtral")) return ChatTemplate.Mistral;
            if (arch.Contains("gemma")) return ChatTemplate.Gemma;
            if (arch.Contains("phi")) return ChatTemplate.Phi;
            if (arch.Contains("deepseek")) return ChatTemplate.DeepSeek;
            if (arch.Contains("starcoder")) return ChatTemplate.ChatML;
            if (arch.Contains("smollm") || arch.Contains("granite") || arch.Contains("nemotron")) return ChatTemplate.ChatML;
            if (arch.Contains("chatml")) return ChatTemplate.ChatML;
        }

        return ChatTemplate.ChatML; // Universal default fallback for open-weights models
    }

    /// <summary>
    /// Returns the stop tokens for the given template.
    /// </summary>
    public string[] GetStopTokens(ChatTemplate template)
    {
        return template switch
        {
            ChatTemplate.ChatML or ChatTemplate.Qwen or ChatTemplate.DeepSeek => new[] { "<|im_end|>", "<|im_start|>" },
            ChatTemplate.Llama3 => new[] { "<|eot_id|>", "<|end_of_text|>" },
            ChatTemplate.Llama2 => new[] { "[/INST]", "</s>" },
            ChatTemplate.Mistral => new[] { "[/INST]", "</s>" },
            ChatTemplate.Gemma => new[] { "<end_of_turn>", "<eos>" },
            ChatTemplate.Phi => new[] { "<|end|>", "<|endoftext|>" },
            ChatTemplate.CommandR => new[] { "<|END_OF_TURN_TOKEN|>" },
            ChatTemplate.Alpaca => new[] { "### Instruction:", "### Response:", "</s>" },
            ChatTemplate.Vicuna => new[] { "USER:", "ASSISTANT:", "</s>" },
            _ => new[] { "</s>", "\n\nUser:", "\n\nAssistant:" }
        };
    }
}
