using System;
using System.Collections.Generic;

namespace Klydis.Core.Protocol;

/// <summary>
/// Execution policy and context strategy for an agent model.
/// Sits above raw GGUF metadata to declare how the agent loop, context compiler,
/// prompt engine, and tool router optimize their execution for THIS specific model.
/// </summary>
public sealed record AgentModelProfile
{
    /// <summary>Family name (e.g. "qwen35", "llama3", "deepseek", "mistral").</summary>
    public required string ModelFamily { get; init; }

    /// <summary>Protocol family name (e.g. "qwen-native", "generic-json", "antml").</summary>
    public required string ProtocolFamily { get; init; }

    /// <summary>Name of the protocol adapter class used for parsing and rendering.</summary>
    public required string ProtocolAdapter { get; init; }

    /// <summary>Reasoning channel behavior (e.g. NativeThinkBlock, Hidden, None).</summary>
    public ReasoningProtocol ReasoningMode { get; init; } = ReasoningProtocol.None;

    /// <summary>Tool calling dialect (e.g. QwenNative, GenericJson).</summary>
    public ToolProtocol ToolCallingMode { get; init; } = ToolProtocol.Unknown;

    /// <summary>Context management strategy name.</summary>
    public string ContextStrategy { get; init; } = "DynamicSlicedBudget";

    /// <summary>Tool selection and ranking strategy.</summary>
    public string ToolSelectionStrategy { get; init; } = "CapabilityRanked";

    /// <summary>Whether the model is permitted to execute multiple tool calls in a single generation.</summary>
    public bool AllowParallelTools { get; init; } = true;

    /// <summary>Max tool calls accepted per generation turn.</summary>
    public int MaxToolCallsPerGeneration { get; init; } = 1;

    /// <summary>Prompt profile key (e.g. "smeagle-action-first", "standard-agentic").</summary>
    public string PromptProfile { get; init; } = "standard-agentic";

    /// <summary>Default sampling temperature.</summary>
    public double DefaultTemperature { get; init; } = 0.7;

    /// <summary>Default top-p sampling value.</summary>
    public double DefaultTopP { get; init; } = 0.9;

    /// <summary>Maximum candidate actions parsed from a single generation.</summary>
    public int MaxActionCandidates { get; init; } = 4;

    /// <summary>Recommended soft working context budget in tokens (e.g. 16,384 for Smeagle).</summary>
    public int RecommendedContextBudget { get; init; } = 16384;

    /// <summary>Hard working context budget ceiling in tokens before aggressive compaction (e.g. 24,576 for Smeagle).</summary>
    public int HardContextBudget { get; init; } = 24576;

    /// <summary>Recovery policy applied when a tool call fails or is blocked.</summary>
    public string RecoveryPolicy { get; init; } = "MechanicalReroute";

    /// <summary>Whether Small-Model Execution Mode is active (1 step, 1 primary capability, compact history, 1 tool/turn).</summary>
    public bool SmallModelExecutionMode { get; init; }

    /// <summary>True when this profile represents the primary Klydis Reference Agent model.</summary>
    public bool IsReferenceModel { get; init; }

    /// <summary>True if model supports native tool-calling tags.</summary>
    public bool SupportsNativeTools { get; init; } = true;

    /// <summary>True if model can reliably produce JSON output conforming to a schema.</summary>
    public bool SupportsStructuredOutput { get; init; } = true;

    /// <summary>True if sampling can be constrained by a GBNF grammar.</summary>
    public bool SupportsGrammar { get; init; } = true;

    /// <summary>True if model continues execution reliably across multiple tool turns.</summary>
    public bool SupportsContinuation { get; init; } = true;

    /// <summary>Preferred tool protocol mode ("native" or "json").</summary>
    public string PreferredToolMode { get; init; } = "native";

    /// <summary>
    /// Canonical profile for Smeagle 4B (Klydis Reference Agent Model).
    /// </summary>
    public static readonly AgentModelProfile Smeagle4B = new()
    {
        ModelFamily = "qwen35",
        ProtocolFamily = "qwen-native",
        ProtocolAdapter = "QwenProtocolAdapter",
        ReasoningMode = ReasoningProtocol.NativeThinkBlock,
        ToolCallingMode = ToolProtocol.QwenNative,
        ContextStrategy = "DynamicSlicedBudget",
        ToolSelectionStrategy = "CapabilityRanked",
        AllowParallelTools = false, // Enforce 1 decisive action per generation for high reliability
        MaxToolCallsPerGeneration = 1,
        PromptProfile = "smeagle-action-first",
        DefaultTemperature = 0.6,
        DefaultTopP = 0.9,
        MaxActionCandidates = 2,
        RecommendedContextBudget = 16384,
        HardContextBudget = 24576,
        RecoveryPolicy = "MechanicalReroute",
        SmallModelExecutionMode = true,
        IsReferenceModel = true,
        SupportsNativeTools = true,
        SupportsStructuredOutput = true,
        SupportsGrammar = true,
        SupportsContinuation = true,
        PreferredToolMode = "native"
    };

    /// <summary>
    /// Resolves or synthesizes the appropriate AgentModelProfile for a loaded model.
    /// </summary>
    public static AgentModelProfile ForModel(string modelId, string architecture, ModelProfile? profile = null)
    {
        string idLower = (modelId ?? string.Empty).ToLowerInvariant();
        string archLower = (architecture ?? string.Empty).ToLowerInvariant();

        if (idLower.Contains("smeagle") || (archLower.Contains("qwen") && (idLower.Contains("4b") || idLower.Contains("3b"))))
        {
            return Smeagle4B;
        }

        bool isSmall = idLower.Contains("0.5b") || idLower.Contains("1b") || idLower.Contains("1.5b") ||
                       idLower.Contains("2b") || idLower.Contains("3b") || idLower.Contains("4b") ||
                       idLower.Contains("mini") || idLower.Contains("small");

        bool isQwen = archLower.Contains("qwen");

        return new AgentModelProfile
        {
            ModelFamily = isQwen ? "qwen35" : archLower,
            ProtocolFamily = profile != null ? profile.PreferredProtocol.ToString().ToLowerInvariant() : "generic-json",
            ProtocolAdapter = isQwen ? "QwenProtocolAdapter" : "GenericJsonProtocolAdapter",
            ReasoningMode = profile?.Reasoning ?? (isQwen ? ReasoningProtocol.NativeThinkBlock : ReasoningProtocol.None),
            ToolCallingMode = profile?.ToolProtocol ?? (isQwen ? ToolProtocol.QwenNative : ToolProtocol.GenericJson),
            AllowParallelTools = !isSmall,
            MaxToolCallsPerGeneration = isSmall ? 1 : 4,
            PromptProfile = isSmall ? "smeagle-action-first" : "standard-agentic",
            DefaultTemperature = 0.7,
            DefaultTopP = 0.9,
            RecommendedContextBudget = isSmall ? 16384 : 32768,
            HardContextBudget = isSmall ? 24576 : 65536,
            SmallModelExecutionMode = isSmall,
            IsReferenceModel = false,
            SupportsNativeTools = profile?.SupportsNativeTools ?? true,
            SupportsStructuredOutput = profile?.SupportsStructuredOutput ?? true,
            SupportsGrammar = profile?.SupportsGrammar ?? true,
            SupportsContinuation = profile?.SupportsToolContinuation ?? true
        };
    }
}
