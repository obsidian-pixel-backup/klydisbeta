using System;
using System.Collections.Generic;
using Klydis.Core.Chat;
using Klydis.Core.Inference;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Builds the immutable <see cref="ModelProfile"/> for a loaded model. Pure and deterministic
/// (no I/O) so it is trivially testable.
///
/// Chat-template resolution order:
///   1. explicit user/model override
///   2. valid embedded GGUF chat_template (the actual template is DATA — the strongest signal)
///   3. declared metadata template family
///   4. known model-family profile (architecture heuristics)
///   5. generic fallback (unknown models are Conversation-capable, tool-capability unknown)
/// </summary>
public static class ModelProfileFactory
{
    /// <summary>
    /// Builds a profile for the given model metadata.
    /// </summary>
    /// <param name="modelId">Model identifier (library id or file name).</param>
    /// <param name="modelPath">Path of the loaded GGUF file.</param>
    /// <param name="architecture">GGUF architecture string (e.g. "qwen35moe", "llama").</param>
    /// <param name="rawChatTemplate">Embedded GGUF chat_template string, when present.</param>
    /// <param name="declaredTemplate">Declared template family from model metadata, when present.</param>
    /// <param name="explicitOverride">Explicit user/model override; highest priority.</param>
    /// <param name="stopTokens">Model-specific stop tokens extracted from GGUF tokenizer metadata.</param>
    public static ModelProfile Build(
        string modelId,
        string modelPath,
        string architecture,
        string? rawChatTemplate = null,
        ChatTemplate? declaredTemplate = null,
        ChatTemplate? explicitOverride = null,
        IReadOnlyList<string>? stopTokens = null)
    {
        ChatTemplate template = ResolveTemplate(architecture, rawChatTemplate, declaredTemplate, explicitOverride);
        ReasoningProtocol reasoning = ResolveReasoning(template, architecture);
        ToolProtocol toolProtocol = ResolveToolProtocol(template, architecture);

        bool isThinking = reasoning == ReasoningProtocol.NativeThinkBlock;
        bool nativeTools = toolProtocol is ToolProtocol.QwenNative
            or ToolProtocol.Llama3Native
            or ToolProtocol.DeepSeekNative
            or ToolProtocol.MistralNative
            or ToolProtocol.GemmaNative
            or ToolProtocol.PhiNative
            or ToolProtocol.CommandRNative;

        bool unknownFamily = template == ChatTemplate.Generic;

        ToolProtocol[] supported = nativeTools
            ? new[] { toolProtocol, ToolProtocol.GenericJson }
            : unknownFamily
                ? Array.Empty<ToolProtocol>()
                : new[] { toolProtocol != ToolProtocol.Unknown ? toolProtocol : ToolProtocol.GenericJson };

        return new ModelProfile
        {
            ModelId = modelId,
            ModelPath = modelPath,
            Architecture = architecture,
            Template = template,
            Reasoning = reasoning,
            ToolProtocol = toolProtocol,
            SupportedProtocols = supported,
            PreferredProtocol = nativeTools ? toolProtocol
                : unknownFamily ? ToolProtocol.Unknown : (toolProtocol != ToolProtocol.Unknown ? toolProtocol : ToolProtocol.GenericJson),
            FallbackProtocols = nativeTools ? new[] { ToolProtocol.GenericJson } : Array.Empty<ToolProtocol>(),
            SupportsNativeTools = nativeTools,
            SupportsStructuredOutput = !unknownFamily,
            SupportsGrammar = nativeTools || toolProtocol == ToolProtocol.GenericJson || toolProtocol == ToolProtocol.OpenAiStyle,
            SupportsThinking = isThinking,
            SupportsToolContinuation = !unknownFamily,
            PreOpensThinkBlock = isThinking,
            RequiresVisibleOutput = isThinking,
            MaxStepThinkingTokens = isThinking ? 4096 : 0,
            StopTokens = stopTokens ?? Array.Empty<string>(),
            ToolCalling = nativeTools ? CapabilityLevel.Usable
                : unknownFamily ? CapabilityLevel.Unsupported : CapabilityLevel.Experimental,
            Continuation = CapabilityLevel.Experimental,
            Repair = CapabilityLevel.Experimental
        };
    }

    /// <summary>
    /// Resolves the chat-template family with the priority order.
    /// </summary>
    public static ChatTemplate ResolveTemplate(
        string architecture,
        string? rawChatTemplate = null,
        ChatTemplate? declaredTemplate = null,
        ChatTemplate? explicitOverride = null)
    {
        // 1. Explicit override
        if (explicitOverride.HasValue) return explicitOverride.Value;

        // 2. Embedded GGUF chat_template
        if (!string.IsNullOrWhiteSpace(rawChatTemplate))
        {
            if (IsQwenThinkingArchitecture(architecture) &&
                rawChatTemplate.Contains("<|im_start|>", StringComparison.Ordinal))
            {
                return ChatTemplate.Qwen;
            }
            var fromEmbedded = DetectFromEmbeddedTemplate(rawChatTemplate);
            if (fromEmbedded.HasValue) return fromEmbedded.Value;
        }

        // 3. Declared metadata family
        if (declaredTemplate.HasValue) return declaredTemplate.Value;

        // 4. Known model-family profile (architecture heuristics)
        if (!string.IsNullOrWhiteSpace(architecture))
        {
            var family = ResolveFamilyFromArchitecture(architecture);
            if (family.HasValue) return family.Value;
        }

        // 5. Generic fallback
        return ChatTemplate.Generic;
    }

    /// <summary>
    /// Maps a known GGUF architecture string to its closest chat-template family.
    /// </summary>
    public static ChatTemplate? ResolveFamilyFromArchitecture(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture)) return null;

        if (architecture.Contains("qwen", StringComparison.OrdinalIgnoreCase))
            return ChatTemplate.Qwen;

        if (architecture.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("dse", StringComparison.OrdinalIgnoreCase))
            return ChatTemplate.DeepSeek;

        if (architecture.Contains("llama", StringComparison.OrdinalIgnoreCase))
            return ChatTemplate.Llama3;

        if (architecture.Contains("mistral", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("mixtral", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("codestral", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("devstral", StringComparison.OrdinalIgnoreCase))
            return ChatTemplate.Mistral;

        if (architecture.Contains("gemma", StringComparison.OrdinalIgnoreCase))
            return ChatTemplate.Gemma;

        if (architecture.Contains("phi", StringComparison.OrdinalIgnoreCase))
            return ChatTemplate.Phi;

        if (architecture.Contains("command", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("cohere", StringComparison.OrdinalIgnoreCase))
            return ChatTemplate.CommandR;

        if (architecture.Contains("glm4", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("glm-4", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("chatglm", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("smollm2", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("smollm-2", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("starcoder2", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("granite", StringComparison.OrdinalIgnoreCase) ||
            architecture.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
        {
            return ChatTemplate.ChatML;
        }

        return null;
    }

    /// <summary>Resolves the reasoning protocol from the resolved template + architecture.</summary>
    public static ReasoningProtocol ResolveReasoning(ChatTemplate template, string architecture)
    {
        if ((template == ChatTemplate.Qwen || template == ChatTemplate.ChatML) && IsQwenThinkingArchitecture(architecture))
        {
            return ReasoningProtocol.NativeThinkBlock;
        }
        if (template == ChatTemplate.DeepSeek ||
            (!string.IsNullOrWhiteSpace(architecture) &&
             (architecture.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ||
              architecture.Contains("r1", StringComparison.OrdinalIgnoreCase))))
        {
            return ReasoningProtocol.NativeThinkBlock;
        }
        return ReasoningProtocol.None;
    }

    /// <summary>Resolves the expected tool-call dialect.</summary>
    public static ToolProtocol ResolveToolProtocol(ChatTemplate template, string architecture)
    {
        return template switch
        {
            ChatTemplate.Qwen => ToolProtocol.QwenNative,
            ChatTemplate.Llama3 => ToolProtocol.Llama3Native,
            ChatTemplate.DeepSeek => ToolProtocol.DeepSeekNative,
            ChatTemplate.Mistral => ToolProtocol.MistralNative,
            ChatTemplate.Gemma => ToolProtocol.GemmaNative,
            ChatTemplate.Phi => ToolProtocol.PhiNative,
            ChatTemplate.CommandR => ToolProtocol.CommandRNative,
            ChatTemplate.Generic => ToolProtocol.Unknown,
            _ => ToolProtocol.GenericJson
        };
    }

    /// <summary>Qwen3.x thinking architectures (mirrors InferenceEngine.IsQwenThinkingArchitecture).</summary>
    public static bool IsQwenThinkingArchitecture(string? architecture)
        => InferenceEngine.IsQwenThinkingArchitecture(architecture);

    /// <summary>
    /// Detects the template family from the embedded GGUF chat-template string.
    /// </summary>
    internal static ChatTemplate? DetectFromEmbeddedTemplate(string rawChatTemplate)
    {
        if (rawChatTemplate.Contains("<|im_start|>", StringComparison.Ordinal)) return ChatTemplate.ChatML;
        if (rawChatTemplate.Contains("<|start_header_id|>", StringComparison.Ordinal)) return ChatTemplate.Llama3;
        if (rawChatTemplate.Contains("[INST]", StringComparison.Ordinal) && rawChatTemplate.Contains("<<SYS>>", StringComparison.Ordinal)) return ChatTemplate.Llama2;
        if (rawChatTemplate.Contains("[INST]", StringComparison.Ordinal) || rawChatTemplate.Contains("[AVAILABLE_TOOLS]", StringComparison.Ordinal) || rawChatTemplate.Contains("[TOOL_CALLS]", StringComparison.Ordinal)) return ChatTemplate.Mistral;
        if (rawChatTemplate.Contains("<start_of_turn>", StringComparison.Ordinal)) return ChatTemplate.Gemma;
        if (rawChatTemplate.Contains("<|user|>", StringComparison.Ordinal) || rawChatTemplate.Contains("<|end|>", StringComparison.Ordinal) || rawChatTemplate.Contains("<|system|>", StringComparison.Ordinal)) return ChatTemplate.Phi;
        if (rawChatTemplate.Contains("<|START_OF_TURN_TOKEN|>", StringComparison.Ordinal)) return ChatTemplate.CommandR;
        if (rawChatTemplate.Contains("<｜User｜>", StringComparison.Ordinal) || rawChatTemplate.Contains("<｜begin of sentence｜>", StringComparison.Ordinal) || rawChatTemplate.Contains("<｜tool calls begin｜>", StringComparison.Ordinal)) return ChatTemplate.DeepSeek;
        if (rawChatTemplate.Contains("### Instruction:", StringComparison.Ordinal)) return ChatTemplate.Alpaca;
        if (rawChatTemplate.Contains("USER:", StringComparison.Ordinal) && rawChatTemplate.Contains("ASSISTANT:", StringComparison.Ordinal)) return ChatTemplate.Vicuna;
        return null;
    }
}
