using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Protocol adapter for Meta Llama 3, 3.1, 3.2, and 3.3 models.
/// Implements header-based turn framing, ipython tool result envelope, and eot/eom delimiters.
/// </summary>
public sealed class Llama3ProtocolAdapter : IModelProtocol
{
    private static readonly string[] DefaultStopTokens = new[]
    {
        "<|eot_id|>",
        "<|eom_id|>",
        "<|end_of_text|>"
    };

    private readonly PromptTemplateEngine _promptEngine;

    public ModelProfile Profile { get; }
    public ChatTemplate Template => ChatTemplate.Llama3;

    public Llama3ProtocolAdapter(ModelProfile profile, PromptTemplateEngine? promptEngine = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _promptEngine = promptEngine ?? new PromptTemplateEngine();
    }

    public string BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
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

            string content = msg.Role switch
            {
                ChatRole.Tool => FormatToolResultContent(msg),
                ChatRole.Runtime => BuildRuntimeDirective(msg.Content),
                _ => msg.Content
            };

            sb.Append($"<|start_header_id|>{roleStr}<|end_header_id|>\n\n{content}<|eot_id|>\n");
        }

        sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");

        if (Profile.PreOpensThinkBlock)
        {
            sb.Append("<think>\n");
        }

        return sb.ToString();
    }

    public IReadOnlyList<CanonicalAction> ParseOutput(string rawOutput)
    {
        return ActionDialectParser.ParseCanonical(rawOutput);
    }

    public string FormatToolResult(ChatMessage toolResult)
    {
        return FormatToolResultContent(toolResult);
    }

    private static string FormatToolResultContent(ChatMessage toolResult)
    {
        if (string.IsNullOrEmpty(toolResult.Name))
        {
            return toolResult.Content;
        }

        return $"[Tool Result for '{toolResult.Name}']:\n{toolResult.Content}";
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
