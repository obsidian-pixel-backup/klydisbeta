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
    Tool,
    /// <summary>
    /// Engine-injected orchestration control (self-corrections, continuation notices,
    /// no-action repairs). Distinct from <see cref="User"/> so runtime recovery instructions
    /// are NEVER presented as the user's conversational intent — the observed failure mode
    /// where the model reasoned about a self-correction as if the user had sent it.
    /// Runtime messages are ephemeral (in-memory only; see ChatEngine.IsEngineInjectedMessage)
    /// and render as an explicit control directive, never as a user turn.
    /// </summary>
    Runtime
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
    /// <param name="messages">The chat messages to format.</param>
    /// <param name="template">The chat template style.</param>
    /// <param name="qwenThinking">
    /// When true (Qwen3.5/Qwen3.6 thinking models), the generation prompt is extended with an
    /// OPEN <see cref="&lt;think&gt;"/> block. These models' embedded templates end the prompt
    /// with "&lt;|im_start|&gt;assistant\n&lt;think&gt;\n" — the model is trained to CONTINUE
    /// the opened reasoning block, then close it with "&lt;/think&gt;". Without the opener the
    /// model has to emit the tag itself and degenerates into spamming &lt;think&gt; instead of
    /// reasoning or calling tools.
    /// </param>
    public string ApplyTemplate(IList<ChatMessage> messages, ChatTemplate template, bool qwenThinking = false)
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
                        ChatRole.Runtime => "system",
                        _ => "user"
                    };
                    string content = msg.Role == ChatRole.Tool 
                        ? (string.IsNullOrEmpty(msg.Name) ? $"<tool_response>\n{msg.Content}\n</tool_response>" : $"<tool_response>\n[Tool Result for '{msg.Name}']:\n{msg.Content}\n</tool_response>")
                        : msg.Role == ChatRole.Runtime
                            ? BuildRuntimeDirective(msg.Content)
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
                        ChatRole.Runtime => "system",
                        _ => "user"
                    };
                    string content = msg.Role == ChatRole.Tool && !string.IsNullOrEmpty(msg.Name) 
                        ? $"[Tool Result for '{msg.Name}']:\n{msg.Content}" 
                        : msg.Role == ChatRole.Runtime
                            ? BuildRuntimeDirective(msg.Content)
                            : msg.Content;
                    sb.Append($"<|start_header_id|>{roleStr}<|end_header_id|>\n\n{content}<|eot_id|>\n");
                }
                sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
                break;

            case ChatTemplate.Llama2:
                string llama2Sys = "";
                foreach (var msg in messages)
                {
                    if (msg.Role == ChatRole.System || msg.Role == ChatRole.Runtime)
                    {
                        llama2Sys += (msg.Role == ChatRole.Runtime ? BuildRuntimeDirective(msg.Content) : msg.Content) + "\n\n";
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
                    if (msg.Role == ChatRole.System || msg.Role == ChatRole.Runtime)
                    {
                        mistralSys += (msg.Role == ChatRole.Runtime ? BuildRuntimeDirective(msg.Content) : msg.Content) + "\n\n";
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
                        ChatRole.Runtime => "user",
                        _ => "user"
                    };
                    string content = msg.Role == ChatRole.Tool 
                        ? (!string.IsNullOrEmpty(msg.Name) ? $"[Tool Result for '{msg.Name}']:\n{msg.Content}" : $"[Tool Result]:\n{msg.Content}")
                        : msg.Role == ChatRole.Runtime
                            ? BuildRuntimeDirective(msg.Content)
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
                        ChatRole.Runtime => "system",
                        _ => "user"
                    };
                    string content = msg.Role == ChatRole.Tool 
                        ? (!string.IsNullOrEmpty(msg.Name) ? $"[Tool Result for '{msg.Name}']:\n{msg.Content}" : $"[Tool Result]:\n{msg.Content}")
                        : msg.Role == ChatRole.Runtime
                            ? BuildRuntimeDirective(msg.Content)
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
                        ChatRole.Runtime => "SYSTEM",
                        _ => "USER"
                    };
                    string content = msg.Role == ChatRole.Tool
                        ? $"[Tool Result]: {msg.Content}"
                        : msg.Role == ChatRole.Runtime
                            ? BuildRuntimeDirective(msg.Content)
                            : msg.Content;
                    sb.Append($"<|START_OF_TURN_TOKEN|><|{roleToken}_TOKEN|>{content}<|END_OF_TURN_TOKEN|>");
                }
                sb.Append("<|START_OF_TURN_TOKEN|><|CHATBOT_TOKEN|>");
                break;

            case ChatTemplate.Alpaca:
                string alpacaSys = "";
                foreach (var msg in messages)
                {
                    if (msg.Role == ChatRole.System || msg.Role == ChatRole.Runtime)
                    {
                        alpacaSys += (msg.Role == ChatRole.Runtime ? BuildRuntimeDirective(msg.Content) : msg.Content) + "\n\n";
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
                    if (msg.Role == ChatRole.System || msg.Role == ChatRole.Runtime)
                    {
                        vicunaSys += (msg.Role == ChatRole.Runtime ? BuildRuntimeDirective(msg.Content) : msg.Content) + "\n\n";
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
                        ChatRole.Runtime => "[RUNTIME CONTROL]",
                        _ => msg.Role.ToString()
                    };
                    sb.Append($"{prefix}: {msg.Content}\n");
                }
                sb.Append("Assistant: ");
                break;
        }

        if (qwenThinking)
        {
            sb.Append("<think>\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a runtime-control directive (ChatRole.Runtime) as an explicit, self-labeled
    /// orchestration instruction — never as a user turn. The marker makes it unambiguous to
    /// the model that this is the harness talking, so recovery instructions are obeyed as
    /// control signals instead of being misread as the user's conversational intent (the
    /// observed failure where the model reasoned about a self-correction as a user message).
    /// </summary>
    private static string BuildRuntimeDirective(string content)
        => content.StartsWith("[RUNTIME CONTROL]", StringComparison.Ordinal)
            ? content
            : "[RUNTIME CONTROL] " + content.Trim();

    /// <summary>
    /// Builds the Qwen-native tool-calling prelude — the exact text the embedded Qwen3.5/3.6
    /// chat template emits when a <c>tools</c> list is supplied. Presenting tools in this format
    /// (OpenAI-style JSON schema inside &lt;tools&gt; + the native &lt;tool_call&gt;
    /// &lt;function=...&gt;&lt;parameter=...&gt; calling instructions) makes these models emit their
    /// native tool-call format instead of degenerating, and activates their trained
    /// thinking-with-tools behavior.
    /// </summary>
    /// <param name="toolsJson">The OpenAI-compatible JSON schema array (from ToolExecutor.FormatToolsForPrompt).</param>
    public string BuildQwenToolsPrelude(string toolsJson)
    {
        // The embedded Qwen3.5/3.6 template renders each tool as ONE compact JSON object per
        // line inside <tools> ({{- tool | tojson }} per tool — no array brackets, no commas).
        // FormatToolsForPrompt returns a single indented JSON array, which does NOT match the
        // trained layout and measurably destabilizes tool calling. Render it the way the
        // template does: split the array into per-object compact lines.
        string toolsBlock = toolsJson;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(toolsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var lines = new List<string>();
                foreach (var tool in doc.RootElement.EnumerateArray())
                {
                    lines.Add(tool.GetRawText());
                }
                if (lines.Count > 0) toolsBlock = string.Join("\n", lines);
            }
        }
        catch
        {
            // Not valid JSON — keep the raw text as-is.
        }

        return string.Join("\n", new[]
        {
            "# Tools",
            "",
            "You have access to the following functions:",
            "",
            "<tools>",
            toolsBlock,
            "</tools>",
            "",
            "If you choose to call a function ONLY reply in the following format with NO suffix:",
            "",
            "<tool_call>",
            "<function=example_function_name>",
            "<parameter=example_parameter_1>",
            "value_1",
            "</parameter>",
            "<parameter=example_parameter_2>",
            "This is the value for the second parameter",
            "that can span",
            "multiple lines",
            "</parameter>",
            "</function>",
            "</tool_call>",
            "",
            "<IMPORTANT>",
            "Reminder:",
            "- Function calls MUST follow the specified format: an inner <function=...></function> block must be nested within <tool_call></tool_call> XML tags",
            "- Required parameters MUST be specified",
            "- You may provide optional reasoning for your function call in natural language BEFORE the function call, but NOT after",
            "- All listed tools are REAL and execute with the runtime's full system access ONLY where policy permits — approval policy, task-step policy, workspace policy, schema validation, replay protection, and persistence policy may still reject a call, and a rejected action did NOT execute and must never be described as executed. NEVER simulate, imagine, or fabricate a tool result in plain text — emit a real <tool_call> and wait for the actual returned output",
            "- You have live access through these tools: web search (search_web), URL crawling (crawl_url), system commands (run_command), file operations, and live data tools — subject to the approval, workspace, and task-step policies above. Never claim you lack access or that data is unavailable — call the tool and it will execute for you when policy permits",
            "- If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls",
            "</IMPORTANT>"
        });
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

        // 1.5 Architecture first for the qwen family. Qwen3.x thinking models (qwen35 / qwen35moe
        // / qwen3-next, ...) use ChatML-style embedded templates (<|im_start|>), so the generic
        // ChatML check below would otherwise shadow them — and without ChatTemplate.Qwen the
        // pre-opened <think> block and native <tool_call> tools prelude are never applied, which
        // makes qwen3.6 fall into think-tag spam, flub tool calls, and loop. Architecture is the
        // authoritative base-family signal; callers further gate the thinking behavior on the
        // template's <tool_call> marker, so plain qwen2/2.5 models are unaffected.
        if (!string.IsNullOrWhiteSpace(architecture) &&
            architecture.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return ChatTemplate.Qwen;
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
            ChatTemplate.Llama3 => new[] { "<|eot_id|>", "<|end_of_text|>", "</s>" },
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
