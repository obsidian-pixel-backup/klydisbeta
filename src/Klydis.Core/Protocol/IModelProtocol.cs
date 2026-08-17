using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// The runtime capabilities a protocol may or may not support — used to gate grammar
/// constraints, thinking parsing, multi-call turns, etc. per model.
/// </summary>
public enum RuntimeCapability
{
    ToolCalling,
    ToolContinuation,
    StructuredOutput,
    GrammarConstrainedOutput,
    Thinking
}

/// <summary>
/// The seam that separates HOW a model communicates from WHAT the agent runtime does with it.
/// The runtime talks to a protocol adapter; the adapter translates the model's native dialect
/// (tool tags, JSON envelopes, thinking blocks, stop tokens) into canonical forms.
///
/// P1 note: adapters are being introduced progressively — Qwen first, then GenericJson, then
/// Llama/Mistral/Gemma/DeepSeek. Until an adapter exists for a profile, the runtime falls back
/// to the legacy paths (PromptTemplateEngine + ChatEngine.ParseToolCalls), which the adapters
/// will eventually subsume. The interface is the contract those adapters must satisfy.
/// </summary>
public interface IModelProtocol
{
    /// <summary>The profile this protocol was built for.</summary>
    ModelProfile Profile { get; }

    /// <summary>The chat-template family this protocol renders.</summary>
    ChatTemplate Template { get; }

    /// <summary>
    /// Renders the message list into a generation prompt for this model, including the
    /// model-specific tool prelude and thinking-block handling.
    /// </summary>
    string BuildPrompt(IReadOnlyList<ChatMessage> messages);

    /// <summary>
    /// Normalizes raw model output into canonical actions using this model's dialect. A single
    /// turn may contain several actions (e.g. parallel tool calls), hence the list. Must never
    /// throw — unparseable output yields an empty list (the runtime treats that as a message
    /// or an escalation, exactly as the legacy parser did).
    /// </summary>
    IReadOnlyList<CanonicalAction> ParseOutput(string rawOutput);

    /// <summary>
    /// Formats a tool result back into the model's dialect for the next generation.
    /// </summary>
    string FormatToolResult(ChatMessage toolResult);

    /// <summary>Stop tokens that end a generation for this model.</summary>
    IReadOnlyList<string> GetStopTokens();

    /// <summary>
    /// True when this protocol supports the given capability at (or above) the minimum level.
    /// </summary>
    bool Supports(RuntimeCapability capability, CapabilityLevel minimum);
}
