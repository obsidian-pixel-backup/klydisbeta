using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Protocol adapter for models utilizing generic JSON action envelopes:
/// {"action": "tool_call", "name": "...", "arguments": {...}}
/// Refactored to dynamically bind stop tokens, think-block pre-opening, and profile template.
/// </summary>
public sealed class GenericJsonProtocolAdapter : IModelProtocol
{
    private static readonly string[] DefaultStopTokens = new[]
    {
        "</s>",
        "<|im_end|>",
        "<|endoftext|>"
    };

    private readonly PromptTemplateEngine _promptEngine;

    public ModelProfile Profile { get; }
    public ChatTemplate Template => Profile.Template;

    public GenericJsonProtocolAdapter(ModelProfile profile, PromptTemplateEngine? promptEngine = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _promptEngine = promptEngine ?? new PromptTemplateEngine();
    }

    public string BuildPrompt(IReadOnlyList<ChatMessage> messages)
        => _promptEngine.ApplyTemplate(
            messages as IList<ChatMessage> ?? messages.ToList(),
            Profile.Template,
            qwenThinking: Profile.PreOpensThinkBlock);

    public IReadOnlyList<CanonicalAction> ParseOutput(string rawOutput)
        => ActionDialectParser.ParseCanonical(rawOutput);

    public string FormatToolResult(ChatMessage toolResult)
        => toolResult.Name == null
            ? $"{{\"action_result\": \"{EscapeJson(toolResult.Content)}\"}}"
            : $"{{\"action_result\": {{\"name\": \"{toolResult.Name}\", \"output\": \"{EscapeJson(toolResult.Content)}\"}}}}";

    public IReadOnlyList<string> GetStopTokens()
    {
        if (Profile.StopTokens != null && Profile.StopTokens.Count > 0)
        {
            return Profile.StopTokens.Concat(DefaultStopTokens).Distinct(StringComparer.Ordinal).ToArray();
        }
        return DefaultStopTokens;
    }

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

    private static string EscapeJson(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
