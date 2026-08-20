using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Protocol adapter for Google Gemma 2 and Gemma 3 models.
/// Implements start_of_turn / end_of_turn delimiters and Gemma 3 thought tags.
/// </summary>
public sealed class GemmaProtocolAdapter : IModelProtocol
{
    private static readonly string[] DefaultStopTokens = new[]
    {
        "<end_of_turn>",
        "<eos>",
        "<|im_end|>"
    };

    private readonly PromptTemplateEngine _promptEngine;

    public ModelProfile Profile { get; }
    public ChatTemplate Template => ChatTemplate.Gemma;

    public GemmaProtocolAdapter(ModelProfile profile, PromptTemplateEngine? promptEngine = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _promptEngine = promptEngine ?? new PromptTemplateEngine();
    }

    public string BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();

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

            string content = msg.Role switch
            {
                ChatRole.Tool => FormatToolResult(msg),
                ChatRole.Runtime => BuildRuntimeDirective(msg.Content),
                _ => msg.Content
            };

            sb.Append($"<start_of_turn>{roleStr}\n{content}<end_of_turn>\n");
        }

        sb.Append("<start_of_turn>model\n");

        if (Profile.PreOpensThinkBlock)
        {
            sb.Append("<thought>\n");
        }

        return sb.ToString();
    }

    public IReadOnlyList<CanonicalAction> ParseOutput(string rawOutput)
    {
        return ActionDialectParser.ParseCanonical(rawOutput);
    }

    public string FormatToolResult(ChatMessage toolResult)
    {
        if (!string.IsNullOrEmpty(toolResult.Name))
        {
            return $"[Tool Result for '{toolResult.Name}']:\n{toolResult.Content}";
        }
        return $"[Tool Result]:\n{toolResult.Content}";
    }

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

    private static string BuildRuntimeDirective(string content)
        => content.StartsWith("[RUNTIME CONTROL]", StringComparison.Ordinal)
            ? content
            : "[RUNTIME CONTROL] " + content.Trim();
}
