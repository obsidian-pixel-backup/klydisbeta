using Klydis.Core.Chat;
using Klydis.Core.Inference;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Builds the immutable <see cref="ModelProfile"/> for a loaded model. Pure and deterministic
/// (no I/O) so it is trivially testable.
///
/// Chat-template resolution order (P0-8 review finding — the OLD order was architecture-first,
/// which let a qwen architecture string override a genuinely different embedded template):
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
    public static ModelProfile Build(
        string modelId,
        string modelPath,
        string architecture,
        string? rawChatTemplate = null,
        ChatTemplate? declaredTemplate = null,
        ChatTemplate? explicitOverride = null)
    {
        ChatTemplate template = ResolveTemplate(architecture, rawChatTemplate, declaredTemplate, explicitOverride);
        ReasoningProtocol reasoning = ResolveReasoning(template, architecture);
        ToolProtocol toolProtocol = ResolveToolProtocol(template, architecture);

        bool isThinking = reasoning == ReasoningProtocol.NativeThinkBlock;
        bool nativeTools = toolProtocol == ToolProtocol.QwenNative;

        // Supported/preferred/fallback dialects: a qwen-family model supports both the native
        // format and generic JSON, preferring native; everything else prefers generic JSON.
        ToolProtocol[] supported = nativeTools
            ? new[] { ToolProtocol.QwenNative, ToolProtocol.GenericJson }
            : new[] { ToolProtocol.GenericJson };

        return new ModelProfile
        {
            ModelId = modelId,
            ModelPath = modelPath,
            Architecture = architecture,
            Template = template,
            Reasoning = reasoning,
            ToolProtocol = toolProtocol,
            SupportedProtocols = supported,
            PreferredProtocol = nativeTools ? ToolProtocol.QwenNative : ToolProtocol.GenericJson,
            FallbackProtocols = nativeTools ? new[] { ToolProtocol.GenericJson } : Array.Empty<ToolProtocol>(),
            SupportsNativeTools = nativeTools,
            SupportsStructuredOutput = toolProtocol == ToolProtocol.GenericJson || nativeTools,
            SupportsGrammar = nativeTools, // grammar-constrained qwen-native tool calls
            SupportsThinking = isThinking,
            SupportsToolContinuation = true, // optimistic until the capability probe refines it
            ToolCalling = nativeTools ? CapabilityLevel.Usable : CapabilityLevel.Experimental,
            Continuation = CapabilityLevel.Experimental,
            Repair = CapabilityLevel.Experimental
        };
    }

    /// <summary>
    /// Resolves the chat-template family with the corrected priority order.
    /// </summary>
    public static ChatTemplate ResolveTemplate(
        string architecture,
        string? rawChatTemplate = null,
        ChatTemplate? declaredTemplate = null,
        ChatTemplate? explicitOverride = null)
    {
        // 1. Explicit override — the user/developer said what the model is.
        if (explicitOverride.HasValue) return explicitOverride.Value;

        // 2. Embedded GGUF chat_template — the strongest factual signal. Special case: qwen3.x
        // thinking models embed a ChatML-style template (<|im_start|>) but still need the Qwen
        // family for the native tool protocol — their embedded template is a marker of the
        // family, not a different format. Everything else follows the embedded template.
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

        // 3. Declared metadata family.
        if (declaredTemplate.HasValue) return declaredTemplate.Value;

        // 4. Known model-family profile.
        if (!string.IsNullOrWhiteSpace(architecture) &&
            architecture.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return ChatTemplate.Qwen;
        }

        // 5. Generic fallback: unknown models are conversation-capable; tool capability is
        // probed later, never silently assumed (P0-8 finding).
        return ChatTemplate.Generic;
    }

    /// <summary>Resolves the reasoning protocol from the resolved template + architecture.</summary>
    public static ReasoningProtocol ResolveReasoning(ChatTemplate template, string architecture)
    {
        if (template == ChatTemplate.Qwen && IsQwenThinkingArchitecture(architecture))
        {
            return ReasoningProtocol.NativeThinkBlock;
        }
        return ReasoningProtocol.None;
    }

    /// <summary>Resolves the expected tool-call dialect.</summary>
    public static ToolProtocol ResolveToolProtocol(ChatTemplate template, string architecture)
    {
        if (template == ChatTemplate.Qwen)
        {
            return ToolProtocol.QwenNative;
        }
        return ToolProtocol.GenericJson;
    }

    /// <summary>Qwen3.x thinking architectures (mirrors InferenceEngine.IsQwenThinkingArchitecture).</summary>
    public static bool IsQwenThinkingArchitecture(string? architecture)
        => InferenceEngine.IsQwenThinkingArchitecture(architecture);

    /// <summary>
    /// Detects the template family from the embedded GGUF chat-template string using the
    /// same markers the legacy detector uses. Returns null when no marker matches.
    /// </summary>
    internal static ChatTemplate? DetectFromEmbeddedTemplate(string rawChatTemplate)
    {
        if (rawChatTemplate.Contains("<|im_start|>", StringComparison.Ordinal)) return ChatTemplate.ChatML;
        if (rawChatTemplate.Contains("<|start_header_id|>", StringComparison.Ordinal)) return ChatTemplate.Llama3;
        if (rawChatTemplate.Contains("[INST]", StringComparison.Ordinal) && rawChatTemplate.Contains("<<SYS>>", StringComparison.Ordinal)) return ChatTemplate.Llama2;
        if (rawChatTemplate.Contains("[INST]", StringComparison.Ordinal)) return ChatTemplate.Mistral;
        if (rawChatTemplate.Contains("<start_of_turn>", StringComparison.Ordinal)) return ChatTemplate.Gemma;
        if (rawChatTemplate.Contains("<|user|>", StringComparison.Ordinal) || rawChatTemplate.Contains("<|end|>", StringComparison.Ordinal)) return ChatTemplate.Phi;
        if (rawChatTemplate.Contains("<|START_OF_TURN_TOKEN|>", StringComparison.Ordinal)) return ChatTemplate.CommandR;
        if (rawChatTemplate.Contains("### Instruction:", StringComparison.Ordinal)) return ChatTemplate.Alpaca;
        if (rawChatTemplate.Contains("USER:", StringComparison.Ordinal) && rawChatTemplate.Contains("ASSISTANT:", StringComparison.Ordinal)) return ChatTemplate.Vicuna;
        return null;
    }
}
