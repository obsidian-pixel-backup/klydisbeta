using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Protocol adapter for Microsoft Phi-3, Phi-3.5, and Phi-4 models.
/// Implements &lt;|system|&gt;, &lt;|user|&gt;, &lt;|assistant|&gt;, and &lt;|end|&gt; control tags.
/// </summary>
public sealed class PhiProtocolAdapter : IModelProtocol
{
    private static readonly string[] DefaultStopTokens = new[]
    {
        "<|end|>",
        "<|endoftext|>",
        "<|user|>"
    };

    private readonly PromptTemplateEngine _promptEngine;

    public ModelProfile Profile { get; }
    public ChatTemplate Template => ChatTemplate.Phi;

    public PhiProtocolAdapter(ModelProfile profile, PromptTemplateEngine? promptEngine = null)
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
                ChatRole.System => "system",
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "user",
                ChatRole.Runtime => "system",
                _ => "user"
            };

            string content = msg.Role switch
            {
                ChatRole.Tool => FormatToolResult(msg),
                ChatRole.Runtime => BuildRuntimeDirective(msg.Content),
                _ => msg.Content
            };

            sb.Append($"<|{roleStr}|>\n{content}<|end|>\n");
        }

        sb.Append("<|assistant|>\n");

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
