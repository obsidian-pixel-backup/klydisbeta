using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// How a model exposes its internal reasoning (if at all). Determines whether the runtime
/// encourages think tags, parses them out of the stream, or treats them as hidden.
/// </summary>
public enum ReasoningProtocol
{
    /// <summary>No reasoning channel — the model answers directly (most instruct models).</summary>
    None,

    /// <summary>Hidden/chain-of-thought: the model reasons internally without explicit tags.</summary>
    Hidden,

    /// <summary>Native &lt;think&gt; block (qwen3.x thinking models): the prompt opens the
    /// block and the parser strips it from visible output.</summary>
    NativeThinkBlock
}

/// <summary>
/// How a model expresses tool calls. The runtime normalizes every dialect into a
/// <see cref="CanonicalAction"/> — this enum is which dialect the model is expected to speak.
/// </summary>
public enum ToolProtocol
{
    /// <summary>Unknown until probed — the model gets conversation-only behavior and a
    /// capability probe before autonomous execution.</summary>
    Unknown,

    /// <summary>Qwen-native &lt;tool_call&gt;&lt;function=...&gt;&lt;parameter=...&gt; tags.</summary>
    QwenNative,

    /// <summary>Structured JSON action envelope ({"type":"tool_call",...} / {"action":"..."}).</summary>
    GenericJson,

    /// <summary>Anthropic-style &lt;antml:invoke name="..."&gt; tags.</summary>
    Antml,

    /// <summary>OpenAI-style function_call / tool_calls objects.</summary>
    OpenAiStyle
}

/// <summary>
/// Coarse capability level used to derive execution policy. 0=unsupported, 1=experimental,
/// 2=usable, 3=reliable — so a weak model gets one-tool-per-turn + low temperature + more
/// repair budget, while a reliable model gets parallel tools and larger steps.
/// </summary>
public enum CapabilityLevel
{
    Unsupported = 0,
    Experimental = 1,
    Usable = 2,
    Reliable = 3
}

/// <summary>
/// The immutable per-model runtime profile — the authoritative description of how THIS model
/// communicates with the agent runtime. Built once when the model loads (see
/// <see cref="ModelProfileFactory"/>), consumed by protocol adapters, execution policy, and
/// diagnostics. P1: the agent loop stops guessing per-model behavior from scattered
/// architecture checks and reads this instead.
/// </summary>
public sealed record ModelProfile
{
    /// <summary>Model identifier / file path as shown in the library.</summary>
    public required string ModelId { get; init; }

    /// <summary>Path of the loaded GGUF file.</summary>
    public required string ModelPath { get; init; }

    /// <summary>GGUF architecture string (e.g. "qwen35moe", "llama").</summary>
    public required string Architecture { get; init; }

    /// <summary>Resolved chat template family (resolution order: explicit → embedded GGUF →
    /// metadata → family → generic — see <see cref="ModelProfileFactory"/>).</summary>
    public required ChatTemplate Template { get; init; }

    /// <summary>How the model reasons (drives think-tag handling).</summary>
    public ReasoningProtocol Reasoning { get; init; } = ReasoningProtocol.None;

    /// <summary>Which tool-call dialect the model is expected to emit.</summary>
    public ToolProtocol ToolProtocol { get; init; } = ToolProtocol.Unknown;

    /// <summary>True when the model is expected to emit its native tool-call format.</summary>
    public bool SupportsNativeTools { get; init; }

    /// <summary>True when the model can emit a structured JSON action reliably.</summary>
    public bool SupportsStructuredOutput { get; init; }

    /// <summary>True when the harness may constrain sampling with a GBNF grammar.</summary>
    public bool SupportsGrammar { get; init; }

    /// <summary>True when the model has a thinking channel that must be parsed/stripped.</summary>
    public bool SupportsThinking { get; init; }

    /// <summary>True when the model reliably continues after a tool result (multi-call turns).</summary>
    public bool SupportsToolContinuation { get; init; }

    /// <summary>
    /// Supported tool-call dialects for this model (from template/metadata).
    /// </summary>
    public IReadOnlyList<ToolProtocol> SupportedProtocols { get; init; } = new[] { ToolProtocol.GenericJson };

    /// <summary>
    /// The dialect the runtime should PREFER for this model (the one known to be most
    /// reliable), used by the ProtocolRegistry to pick the adapter.
    /// </summary>
    public ToolProtocol PreferredProtocol { get; init; } = ToolProtocol.GenericJson;

    /// <summary>
    /// Dialects to try when the preferred protocol fails (ordered fallbacks).
    /// </summary>
    public IReadOnlyList<ToolProtocol> FallbackProtocols { get; init; } = Array.Empty<ToolProtocol>();

    /// <summary>
    /// How confident the runtime is that this profile's protocol is empirically reliable for
    /// THIS model (0..1). Distinct from capability: "supports JSON" is metadata; "Klydis has
    /// verified this model reliably produces valid Klydis actions" is measured. Starts at a
    /// conservative default; the capability probe raises it with evidence. Below 0.5 the
    /// runtime uses a more conservative execution policy (one tool per turn, more repair).
    /// </summary>
    public double ProtocolConfidence { get; init; } = 0.4;

    /// <summary>Measured (or probed, or defaulted) reliability of tool-call emission.</summary>
    public CapabilityLevel ToolCalling { get; init; } = CapabilityLevel.Experimental;

    /// <summary>Measured (or probed, or defaulted) reliability of continuing after tool results.</summary>
    public CapabilityLevel Continuation { get; init; } = CapabilityLevel.Experimental;

    /// <summary>Measured (or probed, or defaulted) reliability of recovering from malformed calls.</summary>
    public CapabilityLevel Repair { get; init; } = CapabilityLevel.Experimental;

    /// <summary>Version of the profile schema.</summary>
    public string ProfileVersion { get; init; } = "1";

    /// <summary>Short stable fingerprint for diagnostics and protocol-drift detection.</summary>
    public string Fingerprint =>
        $"{Template}|{Reasoning}|{ToolProtocol}|{ToolCalling}|{Continuation}|{Repair}";
}
