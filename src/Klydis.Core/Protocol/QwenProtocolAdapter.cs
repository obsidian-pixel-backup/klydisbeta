using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// The first IModelProtocol adapter: encodes the Qwen-specific behaviors that previously
/// lived as scattered special cases (native &lt;tool_call&gt; dialect, the open &lt;think&gt;
/// block for thinking models, Qwen chat template, Qwen stop tokens). Keeping them behind this
/// seam means the agent loop no longer needs qwen-specific branches — and other models get
/// the same treatment via their own adapters.
///
/// Deliberately thin: prompt construction delegates to the existing PromptTemplateEngine (the
/// qwen template + thinking opener it already produces) and output normalization delegates to
/// CanonicalActionNormalizer, so this adapter proves the seam shape without duplicating or
/// destabilizing the battle-tested paths. Deeper per-model rendering moves in as adapters for
/// other families land.
/// </summary>
public sealed class QwenProtocolAdapter : IModelProtocol
{
    private readonly PromptTemplateEngine _promptEngine;

    public ModelProfile Profile { get; }
    public ChatTemplate Template => ChatTemplate.Qwen;

    public QwenProtocolAdapter(ModelProfile profile, PromptTemplateEngine? promptEngine = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _promptEngine = promptEngine ?? new PromptTemplateEngine();
    }

    public string BuildPrompt(IReadOnlyList<ChatMessage> messages)
        => _promptEngine.ApplyTemplate(messages as IList<ChatMessage> ?? messages.ToList(), ChatTemplate.Qwen,
            qwenThinking: Profile.Reasoning == ReasoningProtocol.NativeThinkBlock);

    public IReadOnlyList<CanonicalAction> ParseOutput(string rawOutput)
    {
        // P1.6: the adapter delegates to the shared tolerant dialect parser (the same pipeline
        // the legacy fallback uses — parity by construction). Every extracted call becomes a
        // canonical ToolCall carrying its arguments; an empty parse (plain text or unparseable)
        // yields an empty list so the runtime treats it exactly as the legacy path did.
        var parsed = ActionDialectParser.ParseAll(rawOutput);
        if (parsed.Count == 0)
        {
            return Array.Empty<CanonicalAction>();
        }
        var actions = new CanonicalAction[parsed.Count];
        for (int i = 0; i < parsed.Count; i++)
        {
            actions[i] = new CanonicalAction(
                CanonicalActionType.ToolCall,
                parsed[i].Name,
                null,
                null,
                null,
                "qwen-native",
                parsed[i].Arguments);
        }
        return actions;
    }

    public string FormatToolResult(ChatMessage toolResult)
        => toolResult.Name == null
            ? $"<tool_response>\n{toolResult.Content}\n</tool_response>"
            : $"<tool_response>\n[Tool Result for '{toolResult.Name}']:\n{toolResult.Content}\n</tool_response>";

    public IReadOnlyList<string> GetStopTokens() => new[] { "<|im_end|>", "<|endoftext|>" };

    public bool Supports(RuntimeCapability capability, CapabilityLevel minimum)
    {
        int level = capability switch
        {
            RuntimeCapability.ToolCalling => (int)Profile.ToolCalling,
            RuntimeCapability.ToolContinuation => (int)Profile.Continuation,
            RuntimeCapability.StructuredOutput => Profile.SupportsStructuredOutput ? 2 : 0,
            RuntimeCapability.GrammarConstrainedOutput => Profile.SupportsGrammar ? 2 : 0,
            RuntimeCapability.Thinking => Profile.SupportsThinking ? 2 : 0,
            _ => 0
        };
        return level >= (int)minimum;
    }
}
